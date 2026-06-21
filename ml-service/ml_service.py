"""
FastAPI мікросервіс для класифікації фото доріг через навчені моделі.

Перед запуском (локально):
    pip install fastapi uvicorn torch torchvision timm pillow python-multipart piexif google-cloud-storage

Запуск (локально):
    uvicorn ml_service:app --host 0.0.0.0 --port 8000 --reload

На Render моделі автоматично завантажуються з GCS bucket (папка service_models/)
при старті контейнера, у локальну папку models/.

Необхідні Environment Variables на Render:
    BUCKET_NAME        - назва GCS bucket (за замовчуванням kyiv-accessibility-models)
    GCLOUD_KEY_JSON     - Base64-закодований service account ключ (JSON)
"""

import base64
import io
import os
import tempfile
from pathlib import Path

# ─── GCS CREDENTIALS (має виконатись ДО будь-яких звернень до GCS) ──
def setup_gcs_credentials():
    b64 = os.environ.get("GCLOUD_KEY_JSON")
    if b64:
        key_json = base64.b64decode(b64).decode("utf-8")
        tmp = tempfile.NamedTemporaryFile(delete=False, suffix=".json", mode="w")
        tmp.write(key_json)
        tmp.close()
        os.environ["GOOGLE_APPLICATION_CREDENTIALS"] = tmp.name
        print("✓ GCS credentials налаштовано з env-змінної")
    else:
        print("ℹ GCLOUD_KEY_JSON не задано, очікуємо локальний gcloud auth / ADC")

setup_gcs_credentials()

import torch
import torch.nn as nn
import torchvision.transforms as transforms
import timm
from PIL import Image
from PIL.ExifTags import TAGS, GPSTAGS
from fastapi import FastAPI, File, UploadFile, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from google.cloud import storage

# ─── НАЛАШТУВАННЯ ────────────────────────────────────────────
MODELS_DIR = Path("models")
IMG_SIZE = 224
DEVICE = torch.device("cuda" if torch.cuda.is_available() else "cpu")

print(f"Device: {DEVICE}")


# ─── ЗАВАНТАЖЕННЯ МОДЕЛЕЙ З GCS ───────────────────────────────
def download_models_from_gcs():
    bucket_name = os.environ.get("BUCKET_NAME", "kyiv-accessibility-models")
    prefix = "service_models/"

    MODELS_DIR.mkdir(exist_ok=True)

    # Якщо моделі вже завантажені (наприклад локальна розробка) - пропускаємо
    if any(MODELS_DIR.glob("*.pth")):
        print("Моделі вже є локально, пропускаємо завантаження з GCS")
        return

    print(f"Завантажуємо моделі з gs://{bucket_name}/{prefix} ...")
    try:
        client = storage.Client()
        bucket = client.bucket(bucket_name)
        blobs = list(bucket.list_blobs(prefix=prefix))

        if not blobs:
            print(f"  ⚠ Не знайдено жодного файлу за префіксом {prefix}")
            return

        for blob in blobs:
            if blob.name.endswith(".pth"):
                filename = blob.name.split("/")[-1]
                dest = MODELS_DIR / filename
                print(f"  ⬇ {filename} ...")
                blob.download_to_filename(str(dest))

        print(f"Завантажено {len(list(MODELS_DIR.glob('*.pth')))} файлів моделей")
    except Exception as e:
        print(f"  ✗ Помилка завантаження моделей з GCS: {e}")


# ─── АРХІТЕКТУРИ ─────────────────────────────────────────────

class DinoClassifierBN(nn.Module):
    """DINOv2 з BatchNorm — для surface_type і surface_quality"""

    def __init__(self, num_classes):
        super().__init__()
        self.backbone = torch.hub.load("facebookresearch/dinov2", "dinov2_vits14")
        for param in self.backbone.parameters():
            param.requires_grad = False
        self.classifier = nn.Sequential(
            nn.Linear(self.backbone.embed_dim, 256),
            nn.BatchNorm1d(256),
            nn.ReLU(),
            nn.Dropout(0.3),
            nn.Linear(256, num_classes)
        )

    def forward(self, x):
        return self.classifier(self.backbone(x))


def build_efficientnet(num_classes):
    model = timm.create_model("efficientnet_b0", pretrained=False)
    in_features = model.classifier.in_features
    model.classifier = nn.Sequential(
        nn.Dropout(0.3),
        nn.Linear(in_features, 256),
        nn.ReLU(),
        nn.Dropout(0.2),
        nn.Linear(256, num_classes)
    )
    return model


def build_dino_sequential(num_classes):
    """DINOv2 збережений як nn.Sequential (ramp/width/lit/tactile)"""
    backbone = torch.hub.load("facebookresearch/dinov2", "dinov2_vits14")
    for param in backbone.parameters():
        param.requires_grad = False
    return nn.Sequential(
        backbone,
        nn.Linear(backbone.embed_dim, 256),
        nn.ReLU(),
        nn.Dropout(0.3),
        nn.Linear(256, num_classes)
    )


# ─── КОНФІГУРАЦІЯ МОДЕЛЕЙ ────────────────────────────────────
# ВАЖЛИВО: "file" має точно відповідати назві .pth файлу
# в gs://BUCKET_NAME/service_models/
MODEL_CONFIGS = {
    "smoothness": {
        "file": "smooth_efficientnet.pth",
        "classes": {0: "severe", 1: "slight", 2: "smooth"},
        "arch": "efficientnet",
    },
    "ramp": {
        "file": "ramp_dinov2.pth",
        "classes": {0: "no_ramp", 1: "ramp"},
        "arch": "dino_seq",
    },
    "width": {
        "file": "width_dinov2.pth",
        "classes": {0: "narrow", 1: "wide"},
        "arch": "dino_seq",
    },
    "lit": {
        "file": "lit_dinov2.pth",
        "classes": {0: "unlit", 1: "lit"},
        "arch": "dino_seq",
    },
    "tactile_paving": {
        "file": "tactile_dinov2.pth",
        "classes": {0: "no", 1: "yes"},
        "arch": "dino_seq",
    },
    "surface_type": {
        "file": "dinov2_surface_type.pth",
        "classes": {0: "asphalt", 1: "concrete", 2: "sett", 3: "paving_stones", 4: "unpaved"},
        "arch": "dino_bn",
    },
    "surface_quality": {
        "file": "dinov2_surface_quality.pth",
        "classes": {0: "excellent", 1: "good", 2: "intermediate", 3: "bad", 4: "very_bad"},
        "arch": "dino_bn",
    },
}

# ─── ЗАВАНТАЖЕННЯ МОДЕЛЕЙ ────────────────────────────────────
loaded_models = {}


def load_all_models():
    print("Завантажуємо моделі в пам'ять...")
    for task, config in MODEL_CONFIGS.items():
        path = MODELS_DIR / config["file"]
        if not path.exists():
            print(f"  ⚠ {task}: файл не знайдено ({path})")
            continue
        try:
            num_classes = len(config["classes"])
            arch = config["arch"]

            if arch == "efficientnet":
                model = build_efficientnet(num_classes)
            elif arch == "dino_seq":
                model = build_dino_sequential(num_classes)
            elif arch == "dino_bn":
                model = DinoClassifierBN(num_classes)

            model.load_state_dict(torch.load(path, map_location=DEVICE))
            model.eval()
            model.to(DEVICE)
            loaded_models[task] = model
            print(f"  ✓ {task} завантажено")
        except Exception as e:
            print(f"  ✗ {task}: помилка — {e}")

    print(f"Завантажено {len(loaded_models)}/{len(MODEL_CONFIGS)} моделей")


# ─── EXIF GPS ────────────────────────────────────────────────
def get_gps_from_exif(image: Image.Image):
    """Витягує GPS координати з EXIF даних фото. Повертає (lat, lon) або None."""
    try:
        exif_data = image._getexif()
        if not exif_data:
            return None

        gps_info = {}
        for tag_id, value in exif_data.items():
            tag = TAGS.get(tag_id, tag_id)
            if tag == "GPSInfo":
                for gps_tag_id, gps_value in value.items():
                    gps_tag = GPSTAGS.get(gps_tag_id, gps_tag_id)
                    gps_info[gps_tag] = gps_value

        if not gps_info:
            return None

        def convert_to_degrees(value):
            d, m, s = value
            return float(d) + float(m) / 60 + float(s) / 3600

        lat = convert_to_degrees(gps_info.get("GPSLatitude", (0, 0, 0)))
        lon = convert_to_degrees(gps_info.get("GPSLongitude", (0, 0, 0)))

        if gps_info.get("GPSLatitudeRef") == "S":
            lat = -lat
        if gps_info.get("GPSLongitudeRef") == "W":
            lon = -lon

        return {"lat": round(lat, 7), "lon": round(lon, 7)}
    except Exception:
        return None


# ─── ТРАНСФОРМАЦІЯ ───────────────────────────────────────────
transform = transforms.Compose([
    transforms.Resize((IMG_SIZE, IMG_SIZE)),
    transforms.ToTensor(),
    transforms.Normalize([0.485, 0.456, 0.406], [0.229, 0.224, 0.225]),
])

# ─── FASTAPI ─────────────────────────────────────────────────
app = FastAPI(title="Road Classifier API")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.on_event("startup")
async def startup():
    download_models_from_gcs()
    load_all_models()


@app.get("/health")
def health():
    return {
        "status": "ok",
        "device": str(DEVICE),
        "loaded_models": list(loaded_models.keys()),
        "missing_models": [t for t in MODEL_CONFIGS if t not in loaded_models],
    }


@app.post("/classify")
async def classify(file: UploadFile = File(...), models: str = ""):
    if not file.content_type.startswith("image/"):
        raise HTTPException(status_code=400, detail="Файл має бути зображенням")

    contents = await file.read()
    try:
        image = Image.open(io.BytesIO(contents)).convert("RGB")
    except Exception:
        raise HTTPException(status_code=400, detail="Не вдалося відкрити зображення")

    # GPS з EXIF
    raw_image = Image.open(io.BytesIO(contents))
    gps = get_gps_from_exif(raw_image)

    tensor = transform(image).unsqueeze(0).to(DEVICE)
    models_to_run = loaded_models
    if models:
        requested = set(models.split(','))
        models_to_run = {k: v for k, v in loaded_models.items() if k in requested}

    results = {}
    for task, model in models_to_run.items():
        config = MODEL_CONFIGS[task]
        try:
            with torch.no_grad():
                output = model(tensor)
                probs = torch.softmax(output, dim=1)[0]
                pred = probs.argmax().item()

            pred_class = config["classes"][pred]
            confidence = round(probs[pred].item(), 4)
            all_probs = {config["classes"][i]: round(probs[i].item(), 4)
                         for i in range(len(config["classes"]))}

            results[task] = {
                "class": pred_class,
                "confidence": confidence,
                "all_probs": all_probs,
            }
        except Exception as e:
            results[task] = {"error": str(e)}

    return {
        "filename": file.filename,
        "gps": gps,
        "results": results,
    }


if __name__ == "__main__":
    import uvicorn

    port = int(os.environ.get("PORT", 8000))
    uvicorn.run(app, host="0.0.0.0", port=port)
