# 🎯 KẾ HOẠCH PHÁT TRIỂN — FaceCheckin System (TODO)

> Cập nhật: 2026-03-12

## 📍 Trạng thái tổng quan

| Hạng mục | Trạng thái |
|----------|-----------|
| Backend (main.py) | ✅ Hoàn thành — Entry point ~50 dòng + 11 core/api modules |
| Modal GPU Worker | ✅ Hoàn thành — v1 + v2 |
| Unity Client | ✅ Hoàn thành — MediaPipe + Dashboard |
| Firebase | ✅ Hoàn thành — Firestore + Storage |
| Google Sheets Sync | ✅ Hoàn thành — v2 (Teacher OAuth), v1 đã xóa |
| GCloud Hosting | ✅ Đã deploy — Cloud Run trên project phamgiahuy-9ab1c |
| Modal Cold Start | ✅ Class-based refactor đã giảm 30-60s → ~9s (cold), <1s (warm) |

---

## ✅ Đã hoàn thành

### Hệ thống cốt lõi
- [x] Refactor module hóa Flask server (tách 11 files API & Core từ file gốc >2500 dòng)
- [x] Tích hợp Modal GPU Worker (RetinaFace + ArcFace + YOLOv8)
- [x] FAISS vector search (Cosine Similarity) với ID-Aware grouping
- [x] Endpoint `/verify` — nhận diện khuôn mặt (multi-frame support)
- [x] Endpoint `/register` — đăng ký khuôn mặt (multi-image)
- [x] Continuous Learning (tự học khi match ≥ 98%)
- [x] Quality Filter (MagFace-style, reject confidence < 0.90)
- [x] Anti-fraud: YOLOv8 detect phone + multi-person
- [x] Attendance logging (1 MSSV / 1 ngày)
- [x] Feedback loop (`/correct-identity`)

### Authentication & User Management
- [x] Login / Register / Change Password
- [x] Forgot Password (OTP qua Email)
- [x] Google OAuth login

### Data Management
- [x] Roster import (Excel)
- [x] Profile management (CRUD)
- [x] Avatar URL từ Firebase Storage

### Google Sheets Integration (v2 — Teacher OAuth)
- [x] OAuth connect cho teacher
- [x] Tạo spreadsheet tự động (teacher tự tạo trên Drive của họ)
- [x] Update attendance theo tuần/phase (`/gsheets/update_week`)
- [x] Finalize attendance (`/gsheets/finalize_v2`)
- [x] Xóa hệ thống v1 (Service Account) — 666 dòng, 22+ hàm, 2 routes

### Unity Client
- [x] MediaPipe Face Mesh (468 landmarks, local detection)
- [x] IoU Tracking (tránh gửi request trùng)
- [x] Dashboard UI (UI Toolkit)
- [x] Camera capture + Base64 encode + HTTP send

### DevOps & Tooling
- [x] Dockerfile cho Cloud Run
- [x] Modal GPU Worker deploy script (gpu_worker_v2.py)
- [x] Tạo PROJECT_OVERVIEW.md (tài liệu tổng hợp dự án)
- [x] Tạo Vibe Coding Protocol (.agent/skills/)
- [x] Tạo lessons.md (nhật ký bài học)

### Xử lý xung đột OpenCV & Thực hành Unity MCP
- [x] Xóa OpenCVFaceProvider.cs gây lỗi CS0246 (xung đột với MediaPipe)
- [x] Dùng MCP Unity `editor-application-get-state` → IsCompiling=false, không lỗi
- [x] Dùng MCP Unity `console-get-logs` → 0 lỗi CS0246, chỉ có log MCP Hub cũ
- [x] Dùng MCP Unity `editor-application-set-state` → Play Mode OK (IsPlayingOrWillChangePlaymode=true)

---

## ✅ Vừa hoàn thành (2026-02-25)

### Dọn dẹp Google Sheets v1
- [x] Xóa hệ thống Google Sheets v1 (Service Account) — 666 dòng, 22+ hàm, 2 routes
- [x] Xóa `import re` duplicate
- [x] Thêm `@_require_api_key` vào `gsheets_ensure_v2` (bảo mật)
- [x] Sửa bare `except: pass` → log lỗi trong `_ensure_subject_tab_header_v2`

---

## ✅ Vừa hoàn thành (2026-03-10)

### Deploy lên GCloud (Paid Account)
- [x] Upgrade GCloud từ free trial sang paid account
- [x] Deploy backend lên Cloud Run (project: phamgiahuy-9ab1c, region: asia-southeast1)
- [x] Service facecheckin-ai chạy tại: https://facecheckin-ai-tnvesnqd4q-as.a.run.app

---

## 🔥 Cần làm ngay (Priority: HIGH)

### Hosting — Đã chuyển sang GCloud paid (project phamgiahuy-9ab1c)
- [x] Chọn hosting thay thế: Railway / Fly.io / Render
- [x] Cấu hình deployment cho hosting mới
- [x] Migrate environment variables (Firebase keys, SMTP, API key)
- [x] Test toàn bộ API routes sau khi deploy
- [x] Cập nhật MODAL_API_URL nếu cần

### Modal Cold Start — Đã giảm ~9s (class-based), cần thêm prewarm
- [ ] Thêm endpoint `/prewarm-modal` vào main.py (kế hoạch đã tạo, chưa code)
- [ ] Cấu hình Cloud Scheduler ping Modal trước giờ học
- [ ] Tăng timeout `call_modal_ai()` từ 60s → 120s + retry logic (kế hoạch đã tạo, chưa code)

---

## 📋 Backlog (Priority: MEDIUM)

### Tối ưu hiệu năng
- [ ] Viết benchmark script: đo thời gian xử lý 100 frames (Mục tiêu: < 200ms end-to-end)
- [ ] Đánh giá chuyển ArcFace sang ONNX Runtime (thay TensorFlow trên Modal)
- [ ] Giảm Docker image Modal (hiện >3GB do TensorFlow)

### Bảo mật
- [ ] Hash password (bcrypt) thay vì lưu plain text trong Firestore
- [ ] Thêm JWT token thay vì chỉ dùng API key
- [ ] Rate limiting cho các endpoint nhạy cảm

### Testing
- [ ] Viết unit test cho `/verify` — error rate < 1% với ảnh mờ/thiếu sáng
- [ ] Memory leak test: chạy tải giả lập 1 giờ, RAM ≤ 512MB
- [ ] Integration test: register → verify → check attendance log

### Tính năng mới
- [ ] Bật lại Anti-Spoofing (MiniFASNet) khi thư viện ổn định
- [ ] Bật lại GFPGAN (Image Enhancement) khi fix được compatibility
- [ ] Thêm endpoint `/predict` (chỉ trả embedding, không matching)
- [ ] Export attendance report (PDF/Excel)
