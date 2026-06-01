"""
config/settings.py — Tất cả cấu hình ENV, constants, và Firebase initialization.
Được tách từ main.py dòng 1-137.
"""
import os
import threading
from datetime import timezone, timedelta

os.environ["QT_QPA_PLATFORM"] = "offscreen"
os.environ["TF_ENABLE_ONEDNN_OPTS"] = "0"

# ====== GOOGLE CLOUD PROJECT SETTINGS ======
# Dùng cấu hình của project MỚI facecheckin-ai
os.environ["GOOGLE_CLOUD_PROJECT"] = "facecheckin-ai"
FIREBASE_PROJECT_ID = "facecheckin-ai"
# Bucket name matches Firebase default bucket logic
BUCKET_NAME = "facecheckin-ai.firebasestorage.app"
CRED_PATH = "serviceAccountKey.json"

import firebase_admin
from firebase_admin import credentials, firestore, storage
from flask import Flask
from flask_cors import CORS

# --- CẤU HÌNH (ENV) ---
MODEL_NAME = os.getenv("MODEL_NAME", "ArcFace")
DETECTOR_BACKEND = os.getenv("DETECTOR_BACKEND", "retinaface")
VERIFY_DETECTOR_BACKEND = os.getenv("VERIFY_DETECTOR_BACKEND", "yunet")
MAX_VERIFY_FRAMES = int(os.getenv("MAX_VERIFY_FRAMES", "3"))

# Cấu hình FAISS & Search
FAISS_METRIC = os.getenv("FAISS_METRIC", "cosine").lower()
NORMALIZE_ENV = os.getenv("NORMALIZE_EMBEDDINGS", "1").lower()
NORMALIZE_EMBEDDINGS = NORMALIZE_ENV in ("1", "true", "yes")

# Ngưỡng logic Matching
COSINE_MIN = float(os.getenv("COSINE_MIN", "0.55"))
COSINE_GAP_MIN = float(os.getenv("COSINE_GAP_MIN", "0.03"))
COSINE_RATIO_MAX = float(os.getenv("COSINE_RATIO_MAX", "0.97"))

# Warmup & Key
WARMUP_ON_START = os.getenv("WARMUP_ON_START", "1").lower() in ("1", "true", "yes")
API_KEY = os.getenv("API_KEY", "").strip()

# Email Config
SMTP_EMAIL = os.getenv("SMTP_EMAIL", "").strip()
SMTP_PASSWORD = os.getenv("SMTP_PASSWORD", "").strip()
SMTP_HOST = os.getenv("SMTP_HOST", "smtp.gmail.com").strip()
SMTP_PORT = int(os.getenv("SMTP_PORT", "465"))

# Firebase Settings
COLLECTION_NAME = "FaceVectors"
ATT_LOGS_COLLECTION = "attendance_logs"
VN_TZ = timezone(timedelta(hours=7))

# Multi-Tenancy Collections
TEACHERS_COLLECTION = "Teachers"
TERMS_COLLECTION = "terms"
CLASSES_COLLECTION = "classes"

# Google OAuth Config
GOOGLE_CLIENT_ID = os.getenv("GOOGLE_CLIENT_ID", "").strip()
GOOGLE_CLIENT_SECRET = os.getenv("GOOGLE_CLIENT_SECRET", "").strip()
GOOGLE_REDIRECT_URI = os.getenv("GOOGLE_REDIRECT_URI", "").strip()
GOOGLE_SCOPES = [
    "https://www.googleapis.com/auth/spreadsheets",
    "https://www.googleapis.com/auth/drive.file",
    "openid",
    "https://www.googleapis.com/auth/userinfo.email",
]

# --- FLASK APP ---
app = Flask(__name__)
CORS(app)

# --- GLOBAL STATE & LOCK ---
INDEX_LOCK = threading.Lock()
TEACHER_MANAGERS = {}

# --- FIREBASE INIT ---
if not firebase_admin._apps:
    try:
        if os.path.exists(CRED_PATH):
            cred = credentials.Certificate(CRED_PATH)
            firebase_admin.initialize_app(cred, {
                'projectId': FIREBASE_PROJECT_ID,
                'storageBucket': BUCKET_NAME
            })
            print(f"🔥 Firebase initialized with serviceAccountKey.json (project={FIREBASE_PROJECT_ID})")
        else:
            print("⚠️ No serviceAccountKey.json, using Application Default Credentials (ADC)...")
            firebase_admin.initialize_app(options={
                'projectId': FIREBASE_PROJECT_ID,
                'storageBucket': BUCKET_NAME
            })
            print(f"🔥 Firebase initialized with ADC (project={FIREBASE_PROJECT_ID})")
    except Exception as e:
        print(f"⚠️ Warning: Không init được Firebase: {e}")

try:
    db = firestore.client()
    # Log project thực tế mà Firestore client đang dùng để debug
    print(f"✅ Firestore client project: {db.project}")
    bucket = storage.bucket()
except Exception as e:
    print(f"⚠️ Firestore/Storage init error: {e}")
    db = None
    bucket = None
