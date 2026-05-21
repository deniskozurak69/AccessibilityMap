# ============================================================
# Класифікація ширини тротуарів — wide vs narrow
# Адаптовано для Google Cloud Vertex AI + GCS (Кешування на disk)
#
# Структура в GCS bucket:
#   gs://BUCKET_NAME/
#     dataset/
#       width_classification/
#         wide_sidewalks/    <- широкі тротуари (.jpg)
#         narrow_sidewalks/  <- вузькі тротуари (.jpg)
#     models/                <- сюди зберігаються готові моделі
#     results/               <- csv та png результатів
# ============================================================

import os
import io
import random
import argparse
import tempfile
import shutil
import numpy as np
import pandas as pd
import matplotlib

matplotlib.use('Agg')  # Без GUI для серверного середовища Vertex AI
import matplotlib.pyplot as plt
from PIL import Image
from concurrent.futures import ThreadPoolExecutor
from sklearn.model_selection import train_test_split
from sklearn.metrics import f1_score, classification_report

import torch
import torch.nn as nn
import torch.optim as optim
from torch.utils.data import Dataset, DataLoader
import torchvision.transforms as transforms
import torchvision.models as models
import timm

from google.cloud import storage

# ── Аргументи командного рядка ────────────────────────────────
parser = argparse.ArgumentParser()
parser.add_argument('--dataset_path', type=str,
                    default=os.environ.get('DATASET_PATH', ''),
                    help='gs://bucket/dataset/width_classification')
parser.add_argument('--output_path', type=str,
                    default=os.environ.get('OUTPUT_PATH', ''),
                    help='gs://bucket/models')
parser.add_argument('--model_name', type=str,
                    default=os.environ.get('MODEL_NAME', 'width'),
                    help='Базова назва моделі')
args = parser.parse_args()


# ── Парсинг GCS шляхів ────────────────────────────────────────
def parse_gcs_path(gcs_path):
    """gs://bucket/path → (bucket, path)"""
    path = gcs_path.replace('gs://', '')
    parts = path.split('/', 1)
    return parts[0], parts[1] if len(parts) > 1 else ''


BUCKET_NAME, DATASET_PREFIX = parse_gcs_path(args.dataset_path)
_, OUTPUT_PREFIX = parse_gcs_path(args.output_path)
MODEL_NAME = args.model_name

print(f'Bucket:   {BUCKET_NAME}')
print(f'Dataset:  {DATASET_PREFIX}')
print(f'Output:   {OUTPUT_PREFIX}')
print(f'Model:    {MODEL_NAME}')

# ── GCS клієнт ────────────────────────────────────────────────
storage_client = storage.Client()
bucket = storage_client.bucket(BUCKET_NAME)

# ── Налаштування ─────────────────────────────────────────────
BATCH_SIZE = 32
IMG_SIZE = 224
SEED = 42
NUM_EPOCHS = 15
LR = 1e-4

CLASS_DIRS = {
    'wide_sidewalks': 1,
    'narrow_sidewalks': 0,
}
CLASS_NAMES = ['narrow', 'wide']

random.seed(SEED)
np.random.seed(SEED)
torch.manual_seed(SEED)

DEVICE = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
print(f'Device: {DEVICE}')

# Локальні папки на швидкому SSD контейнера Vertex AI
LOCAL_MODELS_DIR = tempfile.mkdtemp()
LOCAL_DATA_DIR = os.path.join(tempfile.gettempdir(), 'width_dataset')
os.makedirs(LOCAL_DATA_DIR, exist_ok=True)

# ── Крок 1: Збір списку файлів з GCS ──────────────────────────
print('\nЗбираємо список файлів з GCS...')
all_files = []

for class_name, label in CLASS_DIRS.items():
    prefix = f'{DATASET_PREFIX}/{class_name}/'
    blobs = list(storage_client.list_blobs(BUCKET_NAME, prefix=prefix))
    jpg_blobs = [b.name for b in blobs if b.name.lower().endswith(('.jpg', '.jpeg', '.png'))]
    print(f'  {class_name} ({label}): {len(jpg_blobs)} файлів')
    for blob_name in jpg_blobs:
        all_files.append((blob_name, label))

df = pd.DataFrame(all_files, columns=['gcs_path', 'label'])
df = df.sample(frac=1, random_state=SEED).reset_index(drop=True)
print(f'Всього знайдено записів: {len(df)}')


# ── Крок 2: Паралельне викачування та валідація на диск ──────
def download_and_validate(row_tuple):
    idx, gcs_path, label = row_tuple
    try:
        blob = bucket.blob(gcs_path)
        data = blob.download_as_bytes()
        with Image.open(io.BytesIO(data)) as img:
            img.convert('RGB')

        # Записуємо на локальний SSD для миттєвого доступу під час навчання
        local_filename = f"{idx}_{os.path.basename(gcs_path)}"
        local_path = os.path.join(LOCAL_DATA_DIR, local_filename)
        with open(local_path, 'wb') as f:
            f.write(data)
        return local_path
    except Exception:
        return None


print('\nКешуємо та перевіряємо файли локально...')
tasks = list(df.itertuples(name=None))  # (index, gcs_path, label)
with ThreadPoolExecutor(max_workers=16) as ex:
    local_paths = list(ex.map(download_and_validate, tasks))

df['local_path'] = local_paths
df = df[df['local_path'].notna()].copy().reset_index(drop=True)
print(f'Завантажено успішно: {len(df)} файлів. Битих/пропущених: {len(tasks) - len(df)}')


# ── Крок 3: Weighted loss для компенсації дисбалансу ─────────
def compute_class_weights(df):
    counts = df['label'].value_counts().sort_index()
    weights = 1.0 / counts
    weights = weights / weights.sum() * len(counts)
    return torch.tensor(weights.values, dtype=torch.float).to(DEVICE)


# ── Крок 4: Локальний Dataset ─────────────────────────────────
class LocalSidewalkDataset(Dataset):
    def __init__(self, df, transform=None):
        self.df = df.reset_index(drop=True)
        self.transform = transform

    def __len__(self):
        return len(self.df)

    def __getitem__(self, idx):
        row = self.df.iloc[idx]
        try:
            with Image.open(row['local_path']) as img:
                image = img.convert('RGB')
        except Exception:
            image = Image.new('RGB', (IMG_SIZE, IMG_SIZE), (0, 0, 0))
        if self.transform:
            image = self.transform(image)
        return image, torch.tensor(int(row['label']), dtype=torch.long)


train_transform = transforms.Compose([
    transforms.Resize((IMG_SIZE, IMG_SIZE)),
    transforms.RandomHorizontalFlip(),
    transforms.RandomRotation(10),
    transforms.ColorJitter(brightness=0.3, contrast=0.3, saturation=0.2),
    transforms.ToTensor(),
    transforms.Normalize([0.485, 0.456, 0.406], [0.229, 0.224, 0.225]),
])

val_transform = transforms.Compose([
    transforms.Resize((IMG_SIZE, IMG_SIZE)),
    transforms.ToTensor(),
    transforms.Normalize([0.485, 0.456, 0.406], [0.229, 0.224, 0.225]),
])


def make_loaders(df):
    train_df, temp_df = train_test_split(
        df, test_size=0.3, random_state=SEED, stratify=df['label'])
    val_df, test_df = train_test_split(
        temp_df, test_size=0.5, random_state=SEED, stratify=temp_df['label'])

    # Завдяки локальним файлам сміливо збільшуємо num_workers та вмикаємо pin_memory
    train_loader = DataLoader(LocalSidewalkDataset(train_df, train_transform),
                              batch_size=BATCH_SIZE, shuffle=True, num_workers=4, pin_memory=True)
    val_loader = DataLoader(LocalSidewalkDataset(val_df, val_transform),
                            batch_size=BATCH_SIZE, shuffle=False, num_workers=4, pin_memory=True)
    test_loader = DataLoader(LocalSidewalkDataset(test_df, val_transform),
                             batch_size=BATCH_SIZE, shuffle=False, num_workers=4, pin_memory=True)
    print(f'  train={len(train_df)}, val={len(val_df)}, test={len(test_df)}')
    return train_loader, val_loader, test_loader


# ── Крок 5: Моделі ───────────────────────────────────────────
def build_model(arch):
    num_classes = 2
    if arch == 'efficientnet':
        model = timm.create_model('efficientnet_b0', pretrained=True)
        in_features = model.classifier.in_features
        model.classifier = nn.Sequential(
            nn.Dropout(0.3),
            nn.Linear(in_features, 256),
            nn.ReLU(),
            nn.Dropout(0.2),
            nn.Linear(256, num_classes)
        )
    elif arch == 'resnet50':
        model = models.resnet50(weights=models.ResNet50_Weights.DEFAULT)
        in_features = model.fc.in_features
        model.fc = nn.Sequential(
            nn.Dropout(0.3),
            nn.Linear(in_features, 256),
            nn.ReLU(),
            nn.Dropout(0.2),
            nn.Linear(256, num_classes)
        )
    elif arch == 'mobilenet':
        model = models.mobilenet_v3_small(weights=models.MobileNet_V3_Small_Weights.DEFAULT)
        in_features = model.classifier[3].in_features
        model.classifier[3] = nn.Linear(in_features, num_classes)
    elif arch == 'dinov2':
        backbone = torch.hub.load('facebookresearch/dinov2', 'dinov2_vits14')
        for param in backbone.parameters():
            param.requires_grad = False
        model = nn.Sequential(
            backbone,
            nn.Linear(384, 256),  # embed_dim для vits14 дорівнює 384
            nn.ReLU(),
            nn.Dropout(0.3),
            nn.Linear(256, num_classes)
        )
    return model.to(DEVICE)


# ── Крок 6: Тренування ───────────────────────────────────────
def train_epoch(model, loader, optimizer, criterion):
    model.train()
    total_loss, correct, total = 0, 0, 0
    for batch_idx, (images, labels) in enumerate(loader):
        images, labels = images.to(DEVICE), labels.to(DEVICE)
        optimizer.zero_grad()
        outputs = model(images)
        loss = criterion(outputs, labels)
        loss.backward()
        optimizer.step()
        total_loss += loss.item()
        correct += (outputs.argmax(1) == labels).sum().item()
        total += labels.size(0)
        if (batch_idx + 1) % 20 == 0:
            print(f'    batch {batch_idx + 1}/{len(loader)} | '
                  f'loss={total_loss / (batch_idx + 1):.3f} | '
                  f'acc={correct / total:.3f}', end='\r')
    print(' ' * 60, end='\r')
    return total_loss / len(loader), correct / total


def eval_epoch(model, loader, criterion):
    model.eval()
    total_loss, correct, total = 0, 0, 0
    all_preds, all_labels = [], []
    with torch.no_grad():
        for images, labels in loader:
            images, labels = images.to(DEVICE), labels.to(DEVICE)
            outputs = model(images)
            loss = criterion(outputs, labels)
            total_loss += loss.item()
            preds = outputs.argmax(1)
            correct += (preds == labels).sum().item()
            total += labels.size(0)
            all_preds.extend(preds.cpu().numpy())
            all_labels.extend(labels.cpu().numpy())
    f1 = f1_score(all_labels, all_preds, average='weighted', zero_division=0)
    return total_loss / len(loader), correct / total, f1, all_preds, all_labels


def upload_to_gcs(local_path, gcs_path):
    """Завантажити локальний файл в GCS бакет"""
    _, blob_name = parse_gcs_path(gcs_path)
    blob = bucket.blob(blob_name)
    blob.upload_from_filename(local_path)
    print(f'  ☁️  Збережено в GCS: {gcs_path}')


def train_model(arch, n_epochs=NUM_EPOCHS):
    print(f'\n{"=" * 50}')
    print(f'  {arch.upper()} | wide vs narrow (2 кл.)')
    print(f'{"=" * 50}')

    train_loader, val_loader, test_loader = make_loaders(df)
    model = build_model(arch)
    weights = compute_class_weights(df)
    criterion = nn.CrossEntropyLoss(weight=weights)

    # Для DINOv2 оптимізуємо лише лінійну голову, для інших — всю модель
    params = [p for p in model.parameters() if p.requires_grad] if arch == 'dinov2' else model.parameters()
    optimizer = optim.Adam(params, lr=LR)
    scheduler = optim.lr_scheduler.StepLR(optimizer, step_size=5, gamma=0.5)

    best_val_f1, best_weights = 0, None

    for epoch in range(1, n_epochs + 1):
        print(f'  Epoch {epoch:02d}/{n_epochs}', end=' | ')
        train_loss, train_acc = train_epoch(model, train_loader, optimizer, criterion)
        val_loss, val_acc, val_f1, _, _ = eval_epoch(model, val_loader, criterion)
        scheduler.step()

        if val_f1 > best_val_f1:
            best_val_f1 = val_f1
            best_weights = {k: v.clone() for k, v in model.state_dict().items()}

        print(f'loss={train_loss:.3f} | train_acc={train_acc:.3f} | '
              f'val_acc={val_acc:.3f} | val_f1={val_f1:.3f}')

    model.load_state_dict(best_weights)
    _, test_acc, test_f1, preds, labels = eval_epoch(model, test_loader, criterion)
    print(f'\n  -> Test Accuracy={test_acc:.4f} | Test F1={test_f1:.4f}')
    print(classification_report(labels, preds, target_names=CLASS_NAMES, zero_division=0))
    return model, test_acc, test_f1


# ── Крок 7: Запуск циклу архітектур ───────────────────────────
ARCHS = ['efficientnet', 'resnet50', 'mobilenet', 'dinov2']
results = []

for arch in ARCHS:
    gcs_save_path = f'gs://{BUCKET_NAME}/{OUTPUT_PREFIX}/{MODEL_NAME}_{arch}.pth'
    local_save_path = os.path.join(LOCAL_MODELS_DIR, f'{MODEL_NAME}_{arch}.pth')

    # Перевіряємо чи модель вже була навчена раніше
    _, blob_name = parse_gcs_path(gcs_save_path)
    if storage_client.bucket(BUCKET_NAME).blob(blob_name).exists():
        print(f'  Пропускаємо {MODEL_NAME}_{arch} — вже існує в GCS')
        continue

    model, test_acc, test_f1 = train_model(arch)
    results.append({'model': arch,
                    'accuracy': round(test_acc, 4),
                    'f1': round(test_f1, 4)})

    # Зберігаємо локально у контейнері та відправляємо в хмару GCS
    torch.save(model.state_dict(), local_save_path)
    upload_to_gcs(local_save_path, gcs_save_path)

    # Чистимо пам'ять відеокарти перед ініціалізацією наступної моделі
    del model
    torch.cuda.empty_cache()

# Копіюємо найкращу модель під окремим системним файлом (зручно для веб-сервісів / ASP.NET)
if results:
    best = max(results, key=lambda x: x['f1'])
    best_local = os.path.join(LOCAL_MODELS_DIR, f'{MODEL_NAME}_{best["model"]}.pth')
    best_gcs = f'gs://{BUCKET_NAME}/{OUTPUT_PREFIX}/{MODEL_NAME}_best.pth'
    upload_to_gcs(best_local, best_gcs)
    print(f'\n  ⭐ Найкраща архітектура: {best["model"]} (F1={best["f1"]})')

# ── Крок 8: Збереження та вивантаження результатів ───────────
if results:
    results_df = pd.DataFrame(results)
    print('\n=== РЕЗУЛЬТАТИ ===')
    print(results_df.to_string(index=False))

    # Зберігаємо метрики у CSV на GCS
    csv_local = os.path.join(LOCAL_MODELS_DIR, f'results_{MODEL_NAME}.csv')
    results_df.to_csv(csv_local, index=False)
    upload_to_gcs(csv_local, f'gs://{BUCKET_NAME}/results/results_{MODEL_NAME}.csv')

    # Будуємо графік та зберігаємо PNG на GCS
    fig, ax = plt.subplots(figsize=(8, 4))
    results_df.plot(x='model', y=['accuracy', 'f1'], kind='bar', ax=ax, rot=0)
    ax.set_title(f'{MODEL_NAME.capitalize()} Classifier — порівняння моделей')
    ax.set_ylim(0, 1)
    ax.grid(axis='y', alpha=0.3)
    plt.tight_layout()
    png_local = os.path.join(LOCAL_MODELS_DIR, f'results_{MODEL_NAME}.png')
    plt.savefig(png_local, dpi=150)
    upload_to_gcs(png_local, f'gs://{BUCKET_NAME}/results/results_{MODEL_NAME}.png')

# Повне очищення локального диска від тимчасових картинок
shutil.rmtree(LOCAL_DATA_DIR, ignore_errors=True)
print('\n✅ Навчання та вивантаження результатів повністю завершено!')
