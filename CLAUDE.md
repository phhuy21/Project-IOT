# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

FaceCheckin System — automated attendance via face recognition for classrooms. Teachers register student faces, then the system identifies and logs attendance when students appear on camera.

**Language**: Project documentation and comments are in Vietnamese. Follow this convention.

## Architecture (3-tier, REST API)

```
Unity Client (camera) → Cloud Run (Flask, logic + FAISS) → Modal (GPU T4, AI models)
                              ↕
                         Firebase (Firestore + Storage)
                              ↕
                         Google Sheets (attendance sync)
```

- **Cloud Run (Flask)**: API gateway, FAISS vector search, matching logic, data management. NO heavy AI models run here — all AI is offloaded to Modal.
- **Modal GPU Worker**: RetinaFace (face detection) → ArcFace (embedding, 512-dim vector) → YOLOv8n (anti-fraud). Uses DeepFace library.
- **Unity Client**: MediaPipe Face Mesh (local 468-landmark tracking), IoU tracking to avoid duplicate requests, UI Toolkit dashboard.
- **Firebase**: Firestore (NoSQL) for embeddings/logs/roster, Storage for face photos.

## Key Files

| File | Role |
|------|------|
| `FaceCheckin_AI/FaceCheckin_AI/main.py` | Entry point (~50 dòng) — register blueprints, warmup |
| `FaceCheckin_AI/FaceCheckin_AI/config/settings.py` | ENV vars, Firebase init, Flask app, global state |
| `FaceCheckin_AI/FaceCheckin_AI/core/faiss_engine.py` | FAISS index load/search, auto-learn |
| `FaceCheckin_AI/FaceCheckin_AI/core/modal_client.py` | call_modal_ai() — giao tiếp Modal GPU |
| `FaceCheckin_AI/FaceCheckin_AI/core/multitenancy.py` | Multi-tenant helpers (teacher/term/class) |
| `FaceCheckin_AI/FaceCheckin_AI/core/helpers.py` | Shared utilities (normalize, auth, attendance) |
| `FaceCheckin_AI/FaceCheckin_AI/api/auth.py` | 5 routes: register, login, password mgmt |
| `FaceCheckin_AI/FaceCheckin_AI/api/verify.py` | 1 route: /verify (core pipeline) |
| `FaceCheckin_AI/FaceCheckin_AI/api/register.py` | 3 routes: /register, /reload, /version |
| `FaceCheckin_AI/FaceCheckin_AI/api/profiles.py` | 7 routes: profiles, avatar, correct, delete, logs |
| `FaceCheckin_AI/FaceCheckin_AI/api/roster.py` | 4 routes: roster CRUD, schedule bundle |
| `FaceCheckin_AI/FaceCheckin_AI/api/gsheets.py` | 11 routes: Google OAuth + Sheets v2 + backfill |
| `FaceCheckin_AI/FaceCheckin_AI/gpu_worker_v2.py` | Modal GPU Worker v2 (active, multi-backend fallback) |
| `FaceCheckin_AI/docs/architecture.md` | Kiến trúc chi tiết với Mermaid diagrams |
| `gui/gui/gui/Assets/UI/DashboardController.cs` | Unity main controller (UI + API calls + camera) |

## Build & Run Commands

### Backend (local)
```bash
cd FaceCheckin_AI/FaceCheckin_AI
python -m venv venv
venv\Scripts\activate        # Windows
pip install -r requirements.txt
python main.py               # Runs at http://localhost:5050
```

### Deploy to Cloud Run
```bash
cd FaceCheckin_AI/FaceCheckin_AI
gcloud run deploy facecheckin-ai --source . --region asia-southeast1 --allow-unauthenticated
```

### Deploy Modal GPU Worker
```bash
pip install modal
modal token new
modal deploy gpu_worker_v2.py
```

### Unity Client
Open `gui/gui/gui` in Unity Hub. Configure server URL in `DashboardController.cs`.

## Module Structure (Refactored)

| Module | Nội dung |
|--------|----------|
| `config/settings.py` | ENV vars, Firebase init, Flask app, global state |
| `core/modal_client.py` | `call_modal_ai()` — gửi ảnh sang GPU worker |
| `core/multitenancy.py` | Helpers lấy teacher/term/class, path Firestore |
| `core/helpers.py` | Normalize, base64, API key decorator, attendance log |
| `core/faiss_engine.py` | `load_teacher_data()`, `auto_learn_face()` |
| `api/auth.py` | 5 routes: Register, Login, Forgot/Reset/Change Password |
| `api/verify.py` | 1 route: `POST /verify` — pipeline chính |
| `api/register.py` | 3 routes: `/register`, `/reload`, `/version` |
| `api/profiles.py` | 7 routes: Profiles CRUD, Avatar, Correct Identity, Logs |
| `api/roster.py` | 4 routes: Roster, Schedule Bundle |
| `api/gsheets.py` | 11 routes: Google OAuth + Sheets v2 + Backfill |

## Multi-Tenancy

Every API request uses headers `X-Teacher-ID`, `X-Term-ID`, `X-Class-ID` to scope data. Firestore path: `Teachers/{teacher_id}/terms/{term_id}/classes/{class_id}/`.

## Face Matching Logic

FAISS IndexFlatIP with cosine similarity. Three conditions must ALL pass:
1. `best_sim >= 0.55` (minimum threshold)
2. `gap >= 0.03` (top-1 must exceed top-2)
3. `ratio <= 0.97` (top-2 must not be too close to top-1)

Results are grouped by MSSV (student ID) to prevent top-1 and top-2 being the same person.

## Coding Conventions

- Python: `snake_case`. C#: `camelCase`.
- **Lazy Import**: Heavy libraries (TensorFlow, PyTorch) must only be imported inside the function that uses them — critical for serverless cold start.
- **Singleton Models**: Initialize AI models once at global scope. NEVER instantiate inside frame-processing loops (causes memory leaks).
- **No background threads on serverless**: Cloud Run throttles CPU when idle. Use external schedulers instead of `threading` + `while True`.
- Follow the Vibe Coding Protocol in `.agent/skills/vibe-coding-protocol/SKILL.md`: plan in `todo.md`, log bugs/lessons in `docs/lessons.md`.
- Max 3 files per single task. If more needed, split into sub-tasks in `todo.md`.
- Do not install new dependencies without asking first.
- Do not rename existing variables/functions/files.

### Luật Cập Nhật Tài Liệu (Documentation Sync Rule)

Sau khi hoàn thành **BẤT KỲ thay đổi nào** (fix bug, thêm tính năng, refactor, deploy, cấu hình...), **BẠN PHẢI** cập nhật các file tài liệu liên quan:

1. **`todo.md`** — Đánh dấu [x] task đã xong, thêm task mới nếu có, cập nhật bảng trạng thái tổng quan.
2. **`docs/memory.md`** — Cập nhật trạng thái hiện tại, thêm vào Timeline nếu là thay đổi quan trọng.
3. **`CLAUDE.md`** — Cập nhật Known Issues nếu vấn đề được giải quyết hoặc phát sinh mới.
4. **`docs/TomTat_DuAn_FaceCheckin.md`** — Cập nhật bảng "Vấn đề còn tồn đọng" và "Ưu tiên tiếp theo".
5. **`docs/lessons.md`** — Ghi lại bài học kinh nghiệm nếu gặp bug hoặc phát hiện pattern quan trọng.

> ⚠️ **KHÔNG ĐƯỢC hoàn thành task mà KHÔNG cập nhật tài liệu.** Tài liệu lỗi thời còn nguy hiểm hơn không có tài liệu — nó gây hiểu nhầm cho cả người và AI agent sau này.

### Luật Giải Trình Thuật Toán (Algorithm Explanation Rule)

Bất cứ khi nào viết logic liên quan đến **thuật toán lõi** (như tiền xử lý ảnh, tính toán vector, học máy), **BẠN PHẢI** thêm comment giải thích rõ:

1. **Tại sao dùng thuật toán/công thức này?** — Giải thích lý do chọn thuật toán, ưu điểm so với các phương án khác, và điều kiện áp dụng.
2. **Đầu vào đã được chuẩn hóa đúng chưa?** — Xác nhận rõ input đã match với yêu cầu của mô hình/thư viện (kích thước ảnh, range giá trị, dtype, thứ tự kênh màu, v.v.).

> ⚠️ **Tuyệt đối KHÔNG gọi hàm thư viện một cách mù quáng (Black-box calling).** Phải hiểu rõ hàm đó làm gì, nhận input gì, trả output gì trước khi sử dụng.

## Known Issues

- ~~GCloud free trial expired~~ → ✅ Đã upgrade sang paid account, deploy trên Cloud Run (project: phamgiahuy-9ab1c)
- ~~Cloud Run cross-project Firebase routing~~ → ✅ Fixed by overriding `GOOGLE_CLOUD_PROJECT` env var in `config/settings.py` before any GCP imports
- Modal cold start đã giảm từ 30-60s → ~9s nhờ class-based refactor. Vẫn cần prewarm endpoint + scheduler để giảm thêm
- Passwords stored as plain text (needs bcrypt)
- Anti-Spoofing (`anti_spoof=False`) and GFPGAN disabled due to library bugs
- `serviceAccountKey.json` contains Firebase credentials — never commit to public repos
