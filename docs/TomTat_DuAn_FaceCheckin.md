# TÓM TẮT DỰ ÁN FACECHECKIN SYSTEM

## 1. Tổng Quan

**FaceCheckin** là hệ thống điểm danh tự động bằng nhận diện khuôn mặt dành cho lớp học.
Giáo viên đăng ký ảnh khuôn mặt sinh viên, sau đó hệ thống tự động nhận diện và ghi nhận điểm danh khi sinh viên xuất hiện trước camera.

---

## 2. Kiến Trúc Hệ Thống (3 tầng, REST API)

```
Unity Client (camera, MediaPipe)
       │
       ▼  POST /verify
Cloud Run (Flask + FAISS vector search)
       │
       ▼  POST (gửi ảnh base64)
Modal GPU Worker (T4 GPU)
  ├── RetinaFace  → phát hiện khuôn mặt
  ├── ArcFace     → trích xuất embedding 512 chiều
  └── YOLOv8n     → chống gian lận (anti-fraud)
       │
       ▼
Firebase (Firestore + Storage)  ←→  Google Sheets (đồng bộ điểm danh)
```

**Mô tả các tầng:**

- **Unity Client**: Ứng dụng desktop cho giáo viên, sử dụng MediaPipe để phát hiện khuôn mặt tại local (CPU), gửi ảnh lên server qua REST API.
- **Cloud Run (Flask)**: API gateway, xử lý logic nghiệp vụ, tìm kiếm vector FAISS, quản lý dữ liệu. KHÔNG chạy mô hình AI nặng — tất cả AI được chuyển sang Modal.
- **Modal GPU Worker**: Chạy các mô hình AI trên GPU T4 (RetinaFace, ArcFace, YOLOv8n) thông qua thư viện DeepFace.
- **Firebase**: Firestore (NoSQL) lưu embedding, hồ sơ sinh viên, log điểm danh. Storage lưu ảnh khuôn mặt.
- **Google Sheets**: Đồng bộ điểm danh qua OAuth, cho phép giáo viên xem/quản lý trên Google Drive.

---

## 3. Các Thành Phần Chi Tiết

### 3.1 Backend — Cloud Run (Flask)

**~2,500 dòng code Python**, chia thành 6 blueprint với **31+ API endpoint**.

#### API Blueprints:

| Blueprint | Số route | Chức năng |
|-----------|----------|-----------|
| `auth.py` | 5 | Đăng ký, đăng nhập, quản lý mật khẩu |
| `verify.py` | 1 | Pipeline nhận diện chính (POST /verify) |
| `register.py` | 3 | Đăng ký khuôn mặt, reload index, version |
| `profiles.py` | 7 | CRUD hồ sơ, avatar, sửa nhận diện, log |
| `roster.py` | 4 | Danh sách lớp, lịch học |
| `gsheets.py` | 11 | Google OAuth + Sheets đồng bộ điểm danh |

#### Core Modules:

| Module | Chức năng |
|--------|-----------|
| `faiss_engine.py` | Tìm kiếm vector FAISS, auto-learning |
| `modal_client.py` | Gọi GPU Worker qua HTTP |
| `multitenancy.py` | Phân quyền theo giáo viên/kỳ/lớp |
| `helpers.py` | Tiện ích: chuẩn hóa vector, auth, timezone VN |

#### Entry Point (`main.py`, ~58 dòng):
- Import Flask app từ config
- Đăng ký 6 blueprint
- Health check endpoint GET `/`
- Warmup thread tùy chọn

---

### 3.2 GPU Worker — Modal (T4)

**File**: `gpu_worker_v2.py` (class-based, đang active)

- **Image**: NVIDIA CUDA 11.8 + Python 3.10
- **GPU**: NVIDIA T4
- **Kiến trúc**: `@app.cls()` + `@modal.enter()` — load model 1 lần khi khởi động
- **Pipeline xử lý**:
  1. Decode ảnh Base64
  2. YOLOv8n → kiểm tra chống gian lận
  3. RetinaFace → phát hiện khuôn mặt
  4. ArcFace → trích xuất embedding 512 chiều
- **Output**: `{embedding, facial_area, liveness, process_time}`

---

### 3.3 Unity Client

**Thư mục**: `gui/gui/gui/`

- **MediaPipe Face Mesh**: Phát hiện 468 điểm trên khuôn mặt (chạy local, CPU)
- **IoU Tracking**: Tránh gửi request trùng lặp cho cùng một khuôn mặt
- **UI Toolkit**: Giao diện dashboard (UXML + USS)
- **File chính**: `DashboardController.cs` (~2,000+ dòng)
  - Quản lý camera, gọi API, hiển thị kết quả điểm danh
  - Các class serialization: RecognitionResponse, AttendanceLog, ScheduleSession

---

### 3.4 Firebase

**Cấu trúc Firestore (Multi-tenant)**:

```
Teachers/{teacher_id}/
  └── terms/{term_id}/
        └── classes/{class_id}/
              ├── FaceVectors/     → embedding khuôn mặt
              ├── roster/          → danh sách sinh viên
              ├── attendance_logs/ → log điểm danh
              └── photos/          → ảnh khuôn mặt (Storage)
```

Mỗi API request sử dụng header `X-Teacher-ID`, `X-Term-ID`, `X-Class-ID` để phân tách dữ liệu.

---

## 4. Logic Nhận Diện Khuôn Mặt

### 4.1 Quy trình nhận diện (POST /verify):

1. Nhận ảnh multi-frame (base64 list) từ Unity Client
2. Gửi sang Modal GPU: RetinaFace → ArcFace → YOLOv8
3. Tìm kiếm FAISS cosine similarity
4. Áp dụng 3 điều kiện matching
5. Nhóm kết quả theo MSSV (mã số sinh viên)
6. Ghi log điểm danh (1 lần/sinh viên/ngày)
7. Auto-learning nếu match ≥ 98%

### 4.2 Ba điều kiện matching (phải thỏa TẤT CẢ):

| Điều kiện | Ngưỡng | Ý nghĩa |
|-----------|--------|----------|
| `best_sim ≥ 0.55` | Tối thiểu | Độ tương đồng phải đủ cao |
| `gap ≥ 0.03` | Khoảng cách | Top-1 phải vượt xa top-2 |
| `ratio ≤ 0.97` | Tỷ lệ | Top-2 không được quá gần top-1 |

### 4.3 Auto-Learning:

Khi kết quả match ≥ 98% confidence, hệ thống tự động bổ sung vector mới vào FAISS index để cải thiện độ chính xác theo thời gian.

---

## 5. Công Nghệ Sử Dụng

| Thành phần | Công nghệ |
|------------|-----------|
| Backend | Python 3.10, Flask 3.0, Gunicorn |
| Vector Search | FAISS (faiss-cpu 1.7.4) |
| AI Models | DeepFace (RetinaFace + ArcFace), YOLOv8n |
| GPU Infrastructure | Modal (NVIDIA T4, CUDA 11.8) |
| Database | Firebase Firestore + Storage |
| Authentication | Firebase Admin SDK |
| Client | Unity (C#), MediaPipe Face Mesh, UI Toolkit |
| Sheets Sync | Google Sheets API v4, OAuth 2.0 |
| Deployment | Cloud Run (Docker), Modal |
| Dependencies | numpy, requests, firebase-admin, google-auth |

---

## 6. Cấu Trúc Thư Mục Dự Án

```
Project IOT/
├── FaceCheckin_AI/FaceCheckin_AI/     ← Backend Server
│   ├── main.py                        ← Entry point
│   ├── config/settings.py             ← Cấu hình
│   ├── api/                           ← 6 blueprint API
│   │   ├── auth.py, verify.py, register.py
│   │   ├── profiles.py, roster.py, gsheets.py
│   ├── core/                          ← Logic nghiệp vụ
│   │   ├── faiss_engine.py, modal_client.py
│   │   ├── multitenancy.py, helpers.py
│   ├── gpu_worker_v2.py               ← Modal GPU Worker
│   ├── requirements.txt               ← Dependencies
│   └── Dockerfile                     ← Cloud Run container
├── gui/gui/gui/                       ← Unity Client
│   └── Assets/UI/
│       ├── DashboardController.cs     ← Controller chính
│       └── Dashboard.uxml            ← Giao diện
├── docs/                              ← Tài liệu
│   ├── architecture.md, memory.md
│   ├── lessons.md, AGENTS.md
├── CLAUDE.md                          ← Hướng dẫn dự án
└── todo.md                            ← Roadmap phát triển
```

---

## 7. Lệnh Build & Deploy

### Local Backend:
```bash
cd FaceCheckin_AI/FaceCheckin_AI
python -m venv venv
venv\Scripts\activate          # Windows
pip install -r requirements.txt
python main.py                 # Chạy tại http://localhost:5050
```

### Deploy Cloud Run:
```bash
gcloud run deploy facecheckin-ai --source . --region asia-southeast1 --allow-unauthenticated
```

### Deploy Modal GPU Worker:
```bash
pip install modal
modal token new
modal deploy gpu_worker_v2.py
```

### Unity Client:
Mở `gui/gui/gui` trong Unity Hub, cấu hình server URL trong `DashboardController.cs`.

---

## 8. Trạng Thái Hiện Tại

### Đã hoàn thành (~97%):
- Pipeline nhận diện hoàn chỉnh (Unity → Cloud Run → Modal → Firebase)
- Multi-tenancy (phân quyền theo giáo viên/kỳ/lớp)
- 31+ API endpoint trên 6 blueprint
- FAISS vector search với auto-learning
- Google Sheets sync v2 (Teacher OAuth)
- Chống gian lận bằng YOLOv8n
- Refactor module (từ 2,500+ dòng 1 file → 11 file)
- GPU Worker v2 (class-based, giảm cold start 30-60s → ~9s)
- Deploy lên GCloud paid account (Cloud Run, project phamgiahuy-9ab1c)
- Tài liệu đầy đủ

### Vấn đề còn tồn đọng:

| Vấn đề | Mức độ | Chi tiết |
|--------|--------|----------|
| Modal cold start ~9s | Đã cải thiện | Class-based refactor: 30-60s → ~9s, warm <1s |
| Mật khẩu plaintext | Cao | Cần hash bằng bcrypt |
| Timeout Modal 60s | Trung bình | 60s đủ cho ~9s cold start, kế hoạch tăng 120s chưa implement |
| Anti-spoofing bị tắt | Trung bình | Lỗi thư viện MiniFASNet |
| GFPGAN bị tắt | Trung bình | Lỗi tương thích |

---

## 9. So Sánh Hiệu Suất: GCloud CPU + FaceNet (cũ) vs Hệ thống hiện tại

> **Hệ thống cũ:** Chỉ deploy trên **GCloud duy nhất**, chạy hoàn toàn trên **CPU** — dùng mỗi **FaceNet** cho cả việc detect lẫn nhận diện khuôn mặt. Không có GPU, không có chống gian lận, không có auto-learning.

### 9.1 Hạ tầng

| Hạng mục | Hệ thống cũ (GCloud CPU) | Hệ thống hiện tại |
|----------|--------------------------|-------------------|
| **Hosting** | GCloud duy nhất (tất cả trên 1 server) | Cloud Run (logic) + Modal (GPU AI) — tách riêng 2 tầng |
| **Phần cứng AI** | CPU only (GCloud) | GPU T4 (Modal) |
| **Mô hình AI** | FaceNet duy nhất | 4 model: RetinaFace + ArcFace + YOLOv8n + MediaPipe |
| **Code structure** | 1 file Python lớn | 11 files module hóa (api/ + core/ + config/) |
| **Database** | Đơn giản | Firebase Firestore (multi-tenant) + Storage |
| **Client** | Gọi API trực tiếp | Unity + MediaPipe local tracking + IoU |

### 9.2 Kiến trúc AI

| Hạng mục | FaceNet trên GCloud CPU | Hệ thống hiện tại (Modal GPU T4) |
|----------|------------------------|----------------------------------|
| **Face detection** | Haar Cascade (OpenCV) — hay miss | RetinaFace → OpenCV → SSD (3 tầng fallback) |
| **Face recognition** | FaceNet (Google, Inception, 512 chiều) | ArcFace (ResNet50, 512 chiều) |
| **Chống gian lận** | Không có | YOLOv8n (detect phone + đếm người) |
| **Anti-spoofing** | Không có | MiniFASNet (tạm tắt do lỗi library) |
| **Vector search** | So sánh tuần tự (brute-force) | FAISS IndexFlatIP (Cosine Similarity) |
| **Auto-learning** | Không có | Tự bổ sung vector khi match ≥ 98% |

### 9.3 Hiệu năng

| Chỉ số | GCloud CPU + FaceNet | Modal GPU T4 + ArcFace | Cải thiện |
|--------|---------------------|------------------------|-----------|
| **Embedding** | 512 chiều (Inception) | 512 chiều (ResNet50) | Cùng chiều, nhưng ArcFace chính xác hơn |
| **Accuracy (LFW)** | ~99.63% | ~99.82% (ArcFace) | +0.19% |
| **Cold start** | 15-30s (CPU load model) | ~9s (GPU, model bake sẵn) | ~60-70% nhanh hơn |
| **Inference/ảnh** | 2-5 giây (CPU) | <1 giây (GPU T4) | ~80% nhanh hơn |
| **Đồng thời** | 1 request/lần (CPU bottleneck) | Nhiều request (GPU parallel) | Scalable |
| **Detect face** | Hay miss (Haar Cascade) | 3 tầng fallback, ít miss hơn | Đáng tin hơn |

### 9.4 Độ chính xác nhận diện

| Hạng mục | GCloud CPU + FaceNet | Hệ thống hiện tại |
|----------|---------------------|-------------------|
| **Matching** | Chỉ so cosine similarity top-1 | 3 điều kiện: `best_sim ≥ 0.55` + `gap ≥ 0.03` + `ratio ≤ 0.97` |
| **ID grouping** | Không — top-1 và top-2 có thể cùng 1 người | ID-Aware: group theo MSSV, tránh nhầm |
| **Quality filter** | Nhận mọi ảnh kể cả mờ/tối | MagFace-style, reject confidence < 0.90 |
| **False positive** | Cao (dễ nhận nhầm) | Thấp hơn nhiều (3 điều kiện + quality filter) |
| **Gian lận** | Không phát hiện được | YOLOv8 detect phone, đếm số người |

### 9.5 Tổng kết

> Hệ thống đã chuyển từ **"1 server GCloud CPU chạy mỗi FaceNet 128 chiều, không chống gian lận, matching đơn giản"** sang **"kiến trúc 2 tầng: Cloud Run (logic) + Modal GPU T4 (AI), dùng ArcFace 512 chiều + RetinaFace + YOLOv8, matching 3 điều kiện + auto-learning"**. Đây là bước nâng cấp toàn diện cả về hạ tầng, tốc độ, độ chính xác, và bảo mật.

---

## 10. Thống Kê Dự Án

| Chỉ số | Giá trị |
|--------|---------|
| Tổng code backend | ~2,500 dòng Python |
| API endpoints | 31+ |
| API Blueprints | 6 |
| Core modules | 4 |
| Unity client | ~2,000+ dòng C# |
| Mô hình AI | 4 (RetinaFace, ArcFace, YOLOv8n, MediaPipe) |
| File tài liệu | 8+ |
| Trạng thái | ~97% hoàn thành |

---

## 11. Ưu Tiên Tiếp Theo

1. **Keep-alive endpoint** — Giảm cold start cho Modal (kế hoạch đã tạo, cần implement)
2. **Tăng timeout** — 60s → 120s cho Modal client + retry logic
3. **Hash mật khẩu** — Chuyển từ plaintext sang bcrypt
4. **Bật lại anti-spoofing** — Sửa lỗi MiniFASNet

---

*Tài liệu được tạo ngày: 12/03/2026 (cập nhật)*
