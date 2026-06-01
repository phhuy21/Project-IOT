# 🧠 BỘ NHỚ DỰ ÁN (PROJECT MEMORY & CONTEXT)

> TÀI LIỆU QUAN TRỌNG: Mọi AI Agent BẮT BUỘC phải đọc file này trước khi đề xuất giải pháp kiến trúc hoặc bắt đầu một module mới để đảm bảo tính nhất quán của toàn bộ hệ thống.

## 1. Mục Đích Cốt Lõi (Core Purpose)
- **Tên dự án:** FaceCheckin System — Hệ thống Điểm danh tự động bằng Nhận diện Khuôn mặt.
- **Mục tiêu:** Xây dựng luồng nhận diện khuôn mặt real-time với độ chính xác cao, phục vụ điểm danh lớp học (giáo viên → sinh viên). Hỗ trợ multi-tenancy (nhiều giáo viên, nhiều học kỳ, nhiều lớp).
- **Tiêu chí tối thượng:** Tốc độ phản hồi nhanh, tối ưu tài nguyên phần cứng, hạn chế cold-start, ổn định trên serverless.

## 2. Kiến Trúc Tổng Thể (System Architecture)

Hệ thống chia **3 tầng** giao tiếp qua REST API:

```
Unity Client ──POST /verify──▶ Cloud Run (Flask) ──POST──▶ Modal (GPU T4)
  (Camera)         │              (Logic + FAISS)            (AI Models)
                   │                    │
                   │                    ▼
                   │              Firebase (Firestore + Storage)
                   │                    │
                   │                    ▼
                   │              Google Sheets (Sync điểm danh)
                   ▼
            Hiển thị kết quả
```

| Tầng | Công nghệ | Vai trò |
|------|-----------|---------|
| **Client** | Unity + MediaPipe Face Mesh + UI Toolkit | Camera capture, local face tracking (IoU), gửi ảnh Base64 |
| **Server** | Flask + FAISS + Firebase Admin (Python) | Logic matching, quản lý dữ liệu, API gateway, Google Sheets sync |
| **AI Worker** | Modal.com + DeepFace + YOLOv8 (GPU T4) | RetinaFace detect → ArcFace embedding → YOLOv8 anti-fraud |
| **Database** | Firebase Firestore + Storage | Lưu embedding vectors, attendance logs, ảnh khuôn mặt, user accounts |

### Luồng dữ liệu chính:
```
Unity (Camera Frame → Crop Face → Base64)
  ══[POST /verify]══▶
Cloud Run (Gửi ảnh tới Modal → Nhận embedding → FAISS Cosine Search → Matching Logic)
  ══[Response JSON]══▶
Unity (Hiển thị: Tên + MSSV + % khớp + Trạng thái)
```

## 3. Quyết Định Kỹ Thuật Bất Di Bất Dịch (Key Immutable Decisions)

### AI Models
- **Face Recognition:** ArcFace (ResNet50, vector 512 chiều) qua DeepFace. Không dùng Haar Cascade, MTCNN, hoặc API trả phí.
- **Face Detection:** RetinaFace (primary) → OpenCV → SSD (fallback chain).
- **Anti-Fraud:** YOLOv8n detect "cell phone" và đếm số người.
- **Client Tracking:** MediaPipe Face Mesh (468 landmarks, chạy local trên Unity).

### Vector Search
- **FAISS IndexFlatIP** (Cosine Similarity), L2 Normalized.
- **Matching Logic:** 3 điều kiện: `best_sim ≥ 0.55` AND `gap ≥ 0.03` AND `ratio ≤ 0.97`.
- **ID-Aware Search:** Group kết quả theo MSSV, tránh top-2 cùng 1 người.

### Deploy & Infrastructure
- **Server:** Docker container trên Cloud Run (✅ project phamgiahuy-9ab1c, region asia-southeast1).
- **AI Worker:** Modal.com (GPU T4, timeout 600s). Class-based architecture với `@modal.enter()` pre-loading.
- **Database:** Firebase Firestore (NoSQL) + Firebase Storage (ảnh).
- **Stateless:** Không lưu temp files trên container. Xử lý in-memory rồi xóa ngay.

### Coding Rules (từ lessons.md)
- **Lazy Import:** Thư viện nặng (TF, PyTorch) chỉ import trong function cần dùng.
- **Singleton Model:** Load model 1 lần global, truyền frame vào inference. KHÔNG khởi tạo model trong vòng lặp.
- **No Background Thread trên Serverless:** Dùng external scheduler thay vì `threading` + `while True`.
- **Xóa Dead Code ngay:** Khi viết v2, PHẢI xóa v1 ngay khi ổn định. Không để song song.

## 4. Cấu Trúc Thư Mục Hiện Tại (Directory Map)

```
Project IOT/
├── FaceCheckin_AI/FaceCheckin_AI/     ← 🔧 BACKEND SERVER
│   ├── main.py                        ← ⭐ Entry Point (~58 dòng, chỉ import + register blueprints)
│   ├── api/                           ← API Blueprints (auth, verify, register, profiles, roster, gsheets)
│   │   └── CLAUDE.md                  ← Context rules cho API layer
│   ├── core/                          ← Core modules (faiss_engine, modal_client, multitenancy, helpers)
│   │   └── CLAUDE.md                  ← Context rules cho Core layer
│   ├── config/                        ← Cấu hình (settings.py)
│   ├── gpu_worker.py                  ← Modal Worker v1 (legacy, KHÔNG DÙNG)
│   ├── gpu_worker_v2.py               ← Modal Worker v2 (Class-based, @modal.enter()) ← ĐANG DÙNG
│   ├── docs/                          ← Architecture Decision Records (ADRs)
│   │   ├── architecture.md
│   │   └── decisions/                 ← ADR-001 (FAISS), ADR-002 (Modal), ADR-003 (Cosine Sim)
│   ├── Dockerfile                     ← Docker config (python:3.10-slim, gunicorn)
│   ├── requirements.txt               ← Dependencies (Flask, FAISS, Firebase, etc.)
│   ├── serviceAccountKey.json         ← Firebase credentials (⚠️ KHÔNG COMMIT)
│   ├── migrate.py                     ← Script migration dữ liệu
│   └── backfill_storage_path.py       ← Script backfill ảnh
│
├── gui/gui/gui/                       ← 🖥️ UNITY CLIENT
│   ├── Assets/UI/
│   │   ├── DashboardController.cs     ← ⭐ Controller chính (UI + API calls + Camera)
│   │   ├── Dashboard.uxml            ← Layout UI (UI Toolkit)
│   │   └── FilePicker.cs             ← Import Excel
│   └── Assets/MediaPipeUnity/
│       └── Tutorial/Tasks/
│           └── FaceLandmarkerRunner.cs ← MediaPipe Face Detection
│
├── .agent/                            ← 🤖 Agent Configuration
│   ├── skills/                        ← Skill definitions (vibe-coding-protocol, delegate-to-claude, delegate-to-gemini)
│   └── workflows/                     ← Automated workflows (deploy, debug, test)
│
├── AGENTS.md                          ← 🤖 Multi-Agent Architecture (Antigravity orchestrator)
├── todo.md                            ← 📋 Kế hoạch & tiến độ
├── lessons.md                         ← 🧠 Bài học kinh nghiệm (4 entries)
├── memory.md                          ← 📌 File này
├── PROJECT_OVERVIEW.md                ← 📖 Tài liệu chi tiết toàn dự án
└── AI_MODELS_SUMMARY.txt              ← 📝 Tóm tắt công nghệ AI
```

## 5. Firebase Data Structure

```
Teachers/{teacher_id}/
  ├── username, password, email
  └── terms/{term_id}/classes/{class_id}/
        ├── FaceVectors/{mssv}    → embedding[], name, mssv, storage_path
        ├── attendance_logs/      → {date}_{mssv}: name, time, status, match_percent
        ├── roster/{mssv}         → name, mssv
        └── photos/               → Firebase Storage reference
```

## 6. Modal Worker — Chi Tiết Kỹ Thuật

> ⚡ Section này được thêm 2026-03-06 do có nhiều thay đổi quan trọng.

### Kiến trúc hiện tại (gpu_worker_v2.py)
- **Class-based** với `@app.cls()` thay vì function đơn lẻ.
- **`@modal.enter()`**: Load tất cả AI models (ArcFace, RetinaFace, YOLOv8) vào GPU VRAM **1 lần duy nhất** khi container start → giảm latency cho request tiếp theo.
- **`@modal.fastapi_endpoint(method="POST")`**: Thay thế `@modal.web_endpoint` (deprecated).
- **Base Image:** `nvidia/cuda:11.8.0-cudnn8-runtime-ubuntu22.04` (Python 3.10).
- **GPU:** T4, timeout 600s.
- **Model bake:** `download_models()` chạy lúc build image → weights đã có sẵn trên disk.

### URL Convention (sau class-based refactor)
```
https://phhuy21--face-recognition-backend-faceinference-process--XXXXX.modal.run
                                         ^^^^^^^^^^^^^
                                    Tên class (lowercase) xuất hiện trong URL
```
⚠️ Khi refactor class name → URL sẽ thay đổi → phải cập nhật `MODAL_API_URL` trong `core/modal_client.py`.

### Pipeline xử lý (trên Modal)
```
Input (Base64 image)
  → Decode → OpenCV imread
  → YOLOv8: Check phone fraud + count persons
  → RetinaFace → [OpenCV fallback] → [SSD fallback]: Detect face
  → ArcFace: Generate 512-dim embedding
  → Return: {embedding, facial_area, confidence, process_time}
```

## 7. Trạng Thái Hiện Tại (Current Status)

> Cập nhật: **2026-03-12**

### ✅ Hoàn thành
- Hệ thống nhận diện end-to-end (Unity → Cloud Run → Modal → Firebase)
- Multi-tenancy (teacher/term/class)
- Authentication (login, register, forgot password, Google OAuth)
- Google Sheets sync v2 (Teacher OAuth — bootstrap, roster sync, weekly attendance update)
- Continuous Learning (auto-learn khi match ≥ 98%)
- Anti-fraud (YOLOv8 detect phone + multi-person)
- Module hóa Flask server (tách 11 files API & Core từ file gốc >2500 dòng)
- Tài liệu dự án (PROJECT_OVERVIEW.md, todo.md, lessons.md, memory.md, AGENTS.md)
- Multi-Agent Architecture (Antigravity + Claude Code + Gemini CLI + Codex CLI + Perplexity)
- Modal Worker v2 class-based refactor (`@modal.enter()` + `@modal.fastapi_endpoint`)
- Xóa Google Sheets v1 dead code (666 dòng, 22+ hàm)
- Upgrade GCloud sang paid account, deploy Cloud Run (project: phamgiahuy-9ab1c)
- Tạo kế hoạch fix Modal Cold Start (prewarm endpoint + timeout increase)

### ⚠️ Vấn đề đang tồn tại
| Vấn đề | Mức độ | Ghi chú |
|--------|--------|---------|
| **Modal Cold Start ~9s** | 🟢 Improved | Class-based `@modal.enter()` giảm 30-60s → ~9s (cold), <1s (warm). Prewarm endpoint chưa implement |
| **Password lưu plain text** | 🟡 High | Cần bcrypt hash |
| **Modal timeout chỉ 60s** | 🟠 Medium | `call_modal_ai()` timeout 60s vẫn đủ cho cold start ~9s, nhưng kế hoạch tăng 120s + retry đã tạo |
| **Anti-Spoofing bị tắt** | 🟠 Medium | Flag `anti_spoof=False` do library bugs |
| **GFPGAN bị tắt** | 🟠 Medium | Comment out do compatibility issues |
| **Person count check tắt** | 🟢 Low | Disabled cho classroom environment (comment out trong gpu_worker_v2.py) |

### 🎯 Ưu tiên tiếp theo
1. **Implement kế hoạch Cold Start** — prewarm endpoint + Cloud Scheduler + timeout 120s + retry
2. **Hash password** (bcrypt) thay vì plain text
3. **Bật lại anti-spoofing** khi library ổn

## 8. Timeline — Lịch Sử Thay Đổi Quan Trọng

> Section này giúp AI agents hiểu bối cảnh dự án đã phát triển như thế nào.

| Thời gian | Sự kiện | Chi tiết |
|-----------|---------|----------|
| **2025-12** | Quyết định offload GPU → Modal | ADR-002: Cloud Run không hỗ trợ GPU, Modal T4 rẻ hơn |
| **2026-02-20** | Lesson: Cold Start trên Serverless | Import nặng ở đầu file → 15s startup. Giải pháp: Lazy Import |
| **2026-02-24** | Lesson: Memory Leak & Background Thread | Object init trong loop + threading trên serverless |
| **2026-02-24** | Refactor module hóa Flask server | Tách main.py (>2500 dòng) → 11 files (api/ + core/ + config/) |
| **2026-02-25** | Xóa Google Sheets v1 | Xóa 666 dòng dead code, 22+ hàm, 2 routes. Giữ v2 (Teacher OAuth) |
| **2026-03-01** | Tích hợp Gemini CLI | Thêm Gemini 3.1 Pro vào multi-agent architecture |
| **2026-03-02** | Hoàn thiện AGENTS.md | Refine multi-agent architecture dựa trên feedback (security, fallback, output contracts) |
| **2026-03-05** | Refactor Modal → class-based | `@modal.web_endpoint` → `@modal.fastapi_endpoint`, fix `DeepFace.build_model("RetinaFace")` bug |
| **2026-03-06** | Cập nhật memory.md | Thêm Timeline, Modal details, current status update |
| **2026-03-10** | Deploy GCloud paid account | Upgrade free trial → paid, deploy Cloud Run (phamgiahuy-9ab1c) |

## 9. Tham Chiếu Nhanh (Quick Reference)

### Files quan trọng cần đọc trước
| Mục đích | File |
|----------|------|
| Hiểu toàn cảnh dự án | `memory.md` (file này) |
| Kế hoạch & backlog | `todo.md` |
| Bài học kinh nghiệm | `lessons.md` |
| Kiến trúc multi-agent | `AGENTS.md` |
| API layer rules | `FaceCheckin_AI/FaceCheckin_AI/api/CLAUDE.md` |
| Core layer rules | `FaceCheckin_AI/FaceCheckin_AI/core/CLAUDE.md` |
| Architecture decisions | `FaceCheckin_AI/docs/decisions/` |

### Lệnh deploy thường dùng
```bash
# Deploy Modal Worker
modal deploy FaceCheckin_AI/FaceCheckin_AI/gpu_worker_v2.py

# Chạy local server
cd FaceCheckin_AI/FaceCheckin_AI && python main.py

# Build Docker image
docker build -t facecheckin-api ./FaceCheckin_AI/FaceCheckin_AI
```
