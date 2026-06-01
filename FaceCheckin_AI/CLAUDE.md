# FaceCheckin_AI Module Context

Thư mục này chứa phần Backend Server (Python/Flask) và cấu hình Modal GPU Worker, đảm nhận logic chính của hệ thống FaceCheckin.

## Vai trò cốt lõi
- Mở API endpoint (Cloud Run) để nhận ảnh khuôn mặt từ Unity Client.
- Gửi tác vụ phân tích ảnh (nhận diện khuôn mặt ArcFace + chống gian lận YOLOv8) sang Modal GPU Worker.
- Tìm kiếm vector tương đồng (FAISS Search) để xác định danh tính sinh viên.
- Ghi nhận lịch sử điểm danh vào Firebase Firestore và đồng bộ với Google Sheets.

## Kiến trúc Thư mục con
- `FaceCheckin_AI/api/`: Các REST API routes (`auth`, `verify`, `register`, `profiles`, `roster`, `gsheets`).
- `FaceCheckin_AI/core/`: Các module vận hành xử lý cốt lõi (`faiss_engine`, `modal_client`, `multitenancy`, `helpers`).
- `FaceCheckin_AI/config/`: Cấu hình hệ thống (biến môi trường, database).
- `FaceCheckin_AI/main.py`: Entry point chính khởi chạy server Flask.
- `FaceCheckin_AI/gpu_worker_v2.py`: Pipeline AI xử lý nhận diện khuôn mặt triển khai trên Modal.com.

## Quy chuẩn Code (Backend Python)
- **Naming convention**: Sử dụng `snake_case` cho tên biến và hàm.
- **Lazy Import**: Các module nặng (TensorFlow, PyTorch) tuyệt đối chỉ được import cục bộ bên trong scope của hàm gọi để tránh tăng cold start time trên serverless.
- KHÔNG tạo background threads (`threading`) hay `while True` loop — Cloud Run sẽ chặn CPU khi không có request.
- Mọi logic tính toán toán học, vector, khoảng cách (FAISS, Cosine similarity) cần phải có **comment giải trình thuật toán** (tại sao dùng và đầu vào đã chuẩn hóa chưa).
