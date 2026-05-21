# ============================================================
# Класифікація типу поверхні — Mapillary датасет (surface_type)
# Адаптовано для Google Cloud Vertex AI + GCS (Кешування на disk)
#
# Структура в GCS bucket:
#   gs://BUCKET_NAME/
#     mapillary_surface/
#       data.csv         <- файл метаданих з колонками mapillary_image_id та surface_type
#       s_256/           <- папка зі знімками
#         100609302385218.jpg
#         ...
#     models/            <- сюди зберігаються готові моделі (.pth)
#     results/           <- результати аналітики (csv та png)
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
                    help='gs://bucket/mapillary_surface')
parser.add_argument('--output_path', type=str,
                    default=os.environ.get('OUTPUT_PATH', ''),
                    help='gs://bucket/models')
parser.add_argument('--model_name', type=str,
                    default=os.environ.get('MODEL_NAME', 'type'),
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
NUM_EPOCHS = 20
LR = 1e-4

SURFACE_TYPE_MAP = {
    'asphalt': 0,
    'paving_stones': 1,
    'sett': 2,
    'unpaved': 3,
    'concrete': 4,
}
CLASS_NAMES = list(SURFACE_TYPE_MAP.keys())

random.seed(SEED)
np.random.seed(SEED)
torch.manual_seed(SEED)

DEVICE = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
print(f'Device: {DEVICE}')

# Локальні папки на швидкому SSD інстансу Vertex AI Custom Job
LOCAL_MODELS_DIR = tempfile.mkdtemp()
LOCAL_DATA_DIR = os.path.join(tempfile.gettempdir(), 'type_dataset')
os.makedirs(LOCAL_DATA_DIR, exist_ok=True)

# ── Крок 3: Завантаження та парсинг CSV метаданих з GCS ──────
print('\nЗавантажуємо CSV метаданих з GCS...')
csv_blob_path = f'{DATASET_PREFIX}/data.csv'
csv_blob = bucket.blob(csv_blob_path)
csv_bytes = csv_blob.download_as_bytes()

df = pd.read_csv(io.BytesIO(csv_bytes))
print(f'Всього записів у початковому CSV: {len(df)}')

# Фільтруємо лише записи, які мають валідну мітку типу поверхні
df = df[df['surface_type'].isin(SURFACE_TYPE_MAP.keys())].copy()
df['filename'] = df['mapillary_image_id'].astype(str) + '.jpg'
df['label'] = df['surface_type'].map(SURFACE_TYPE_MAP)

print(f'Записів після фільтрації за типами: {len(df)}')


# ── Крок 4: Паралельне викачування та валідація фото на диск ──
def download_and_validate_image(row_tuple):
    idx, filename, label = row_tuple
    gcs_image_path = f'{DATASET_PREFIX}/s_256/{filename}'
    try:
        blob = bucket.blob(gcs_image_path)
        data = blob.download_as_bytes()
        with Image.open(io.BytesIO(data)) as img:
            img.convert('RGB')

        # Запобігаємо простоям GPU: пишемо дані на SSD контейнера
        local_path = os.path.join(LOCAL_DATA_DIR, filename)
        with open(local_path, 'wb') as f:
            f.write(data)
        return local_path
    except Exception:
        return None


print('\nКешуємо та валідуємо зображення на локальний SSD...')
tasks = list(df[['filename', 'label']].itertuples(name=None))

with ThreadPoolExecutor(max_workers=16) as executor:
    local_paths = list(executor.map(download_and_validate_image, tasks))

df['local_path'] = local_paths
df = df[df['local_path'].notna()].copy().reset_index(drop=True)
print(f'Успішно завантажено: {len(df)} фото. Битих/відсутніх у GCS: {len(tasks) - len(df)}')

print('\nРозподіл класів типів покриття:')
print(df['surface_type'].value_counts().to_string())


# ── Крок 5: Weighted loss для компенсації дисбалансу класів ──
def compute_class_weights(df_dataset):
    counts = df_dataset['label'].value_counts().sort_index()
    weights = 1.0 / counts
    weights = weights / weights.sum() * len(counts)
    return torch.tensor(weights.values, dtype=torch.float).to(DEVICE)


# ── Крок 6: Локальний Dataset Клас ───────────────────────────
class LocalSurfaceDataset(Dataset):
    def __init__(self, df_data, transform=None):
        self.df = df_data.reset_index(drop=True)
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
        label = torch.tensor(int(row['label']), dtype=torch.long)
        return image, label


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


def make_loaders(df_data):
    train_df, temp_df = train_test_split(
        df_data, test_size=0.3, random_state=SEED, stratify=df_data['label'])
    val_df, test_df = train_test_split(
        temp_df, test_size=0.5, random_state=SEED, stratify=temp_df['label'])

    # Використовуємо pin_memory=True та num_workers=4 завдяки локальному кешу
    train_loader = DataLoader(LocalSurfaceDataset(train_df, train_transform),
                              batch_size=BATCH_SIZE, shuffle=True, num_workers=4, pin_memory=True)
    val_loader = DataLoader(LocalSurfaceDataset(val_df, val_transform),
                            batch_size=BATCH_SIZE, shuffle=False, num_workers=4, pin_memory=True)
    test_loader = DataLoader(LocalSurfaceDataset(test_df, val_transform),
                             batch_size=BATCH_SIZE, shuffle=False, num_workers=4, pin_memory=True)
    print(f'  Розбиття: train={len(train_df)}, val={len(val_df)}, test={len(test_df)}')
    return train_loader, val_loader, test_loader


# ── Крок 7: Визначення моделей ────────────────────────────────
def build_model(arch, num_classes):
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
    return model.to(DEVICE)


# ── Крок 8: Функції тренування ────────────────────────────────
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


def train_model(arch, df_data, num_classes=5, n_epochs=NUM_EPOCHS):
    print(f'\n{"=" * 50}')
    print(f'  {arch.upper()} | surface_type ({num_classes} кл.)')
    print(f'{"=" * 50}')

    train_loader, val_loader, test_loader = make_loaders(df_data)

    model = build_model(arch, num_classes)
    weights = compute_class_weights(df_data)
    criterion = nn.CrossEntropyLoss(weight=weights)
    optimizer = optim.Adam(model.parameters(), lr=LR)
    scheduler = optim.lr_scheduler.StepLR(optimizer, step_size=5, gamma=0.5)

    best_val_f1, best_weights = 0, None

    for epoch in range(1, n_epochs + 1):
        train_loss, train_acc = train_epoch(model, train_loader, optimizer, criterion)
        val_loss, val_acc, val_f1, _, _ = eval_epoch(model, val_loader, criterion)
        scheduler.step()

        if val_f1 > best_val_f1:
            best_val_f1 = val_f1
            best_weights = {k: v.clone() for k, v in model.state_dict().items()}

        print(f'  Epoch {epoch:02d} | loss={train_loss:.3f} | train_acc={train_acc:.3f} | val_f1={val_f1:.3f}')

    model.load_state_dict(best_weights)
    _, test_acc, test_f1, preds, labels = eval_epoch(model, test_loader, criterion)
    print(f'\n  -> Test Accuracy={test_acc:.4f} | Test F1={test_f1:.4f}')
    print(classification_report(labels, preds, target_names=CLASS_NAMES, zero_division=0))

    return model, test_acc, test_f1


# ── Крок 9: Запуск циклу архітектур ───────────────────────────
ARCHS = ['efficientnet', 'resnet50', 'mobilenet']
results = []

for arch in ARCHS:
    gcs_save_path = f'gs://{BUCKET_NAME}/{OUTPUT_PREFIX}/{MODEL_NAME}_{arch}.pth'
    local_save_path = os.path.join(LOCAL_MODELS_DIR, f'{MODEL_NAME}_{arch}.pth')

    # Перевіряємо чи не була ця модель навчена раніше
    _, blob_name = parse_gcs_path(gcs_save_path)
    if storage_client.bucket(BUCKET_NAME).blob(blob_name).exists():
        print(f'  Пропускаємо {MODEL_NAME}_{arch} — вже існує в GCS')
        continue

    model, test_acc, test_f1 = train_model(arch=arch, df_data=df)
    results.append({'model': arch, 'accuracy': round(test_acc, 4), 'f1': round(test_f1, 4)})

    # Локальне збереження та експорт на GCS
    torch.save(model.state_dict(), local_save_path)
    upload_to_gcs(local_save_path, gcs_save_path)

    # Звільняємо VRAM відеокарти перед ініціалізацією наступного бекбону
    del model
    torch.cuda.empty_cache()

# Копіюємо ваги найкращої за F1 архітектури під фіксованим ім'ям _best.pth
if results:
    best = max(results, key=lambda x: x['f1'])
    best_local = os.path.join(LOCAL_MODELS_DIR, f'{MODEL_NAME}_{best["model"]}.pth')
    best_gcs = f'gs://{BUCKET_NAME}/{OUTPUT_PREFIX}/{MODEL_NAME}_best.pth'
    upload_to_gcs(best_local, best_gcs)
    print(f'\n  ⭐ Найкраща архітектура типу покриття: {best["model"]} (F1={best["f1"]})')

# ── Крок 10: Результати та аналітика ──────────────────────────
if results:
    results_df = pd.DataFrame(results)
    print('\n=== РЕЗУЛЬТАТИ ===')
    print(results_df.to_string(index=False))

    # CSV логування на GCS
    csv_local = os.path.join(LOCAL_MODELS_DIR, f'results_{MODEL_NAME}.csv')
    results_df.to_csv(csv_local, index=False)
    upload_to_gcs(csv_local, f'gs://{BUCKET_NAME}/results/results_{MODEL_NAME}.csv')

    # Будування метричного графіка та відправка PNG в GCS
    fig, ax = plt.subplots(figsize=(8, 4))
    results_df.plot(x='model', y=['accuracy', 'f1'], kind='bar', ax=ax, rot=0)
    ax.set_title(f'{MODEL_NAME.capitalize()} Surface Type Classifier — порівняння моделей')
    ax.set_ylim(0, 1)
    ax.grid(axis='y', alpha=0.3)
    plt.tight_layout()
    png_local = os.path.join(LOCAL_MODELS_DIR, f'results_{MODEL_NAME}.png')
    plt.savefig(png_local, dpi=150)
    upload_to_gcs(png_local, f'gs://{BUCKET_NAME}/results/results_{MODEL_NAME}.png')

# Повне очищення дискового простору інстансу від вивантаженого локального датасету
shutil.rmtree(LOCAL_DATA_DIR, ignore_errors=True)
print('\n✅ Навчання за типами покриття повністю завершено!')

