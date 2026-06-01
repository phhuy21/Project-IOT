# GUI Module Context

Thư mục này chứa Giao diện Người dùng (Desktop Application - Unity Client) của hệ thống FaceCheckin. Ứng dụng này chủ yếu được sử dụng bởi giáo viên tại lớp học.

## Tổng quan Kỹ thuật
- **Engine**: Unity 3D
- **UI Framework**: UI Toolkit (UXML + USS cho styling giống web)
- **Face Detection (Local)**: MediaPipe Face Mesh (468 landmarks) - theo dõi khuôn mặt bằng CPU tại local camera với fps cao.
- **Tracking Algorithm**: IoU (Intersection over Union) để theo dõi mặt và chỉ upload ảnh lên server đối với những gương mặt chưa được xử lý.
- **HTTP Client**: Sử dụng `UnityWebRequest` để kết nối tới Cloud Run Backend.

## Luồng hoạt động chính
1. Camera khởi chạy → MediaPipe phát hiện khuôn mặt liên tục (~60fps).
2. Khi khuôn mặt lọt vào khung hình:
   - Hiển thị bounding box canh chỉnh.
   - Cơ chế IoU tracking xác định xem mặt này đã gửi phân tích chưa (nếu người cũ thì chặn tải lại).
   - Crop khuôn mặt và Encode Base64.
3. Gửi request `POST /verify` lên Cloud Run.
4. Xử lý kịch bản trả về: Hiển thị tên, mã số sinh viên, và % trùng khớp trên Dashboard UI.

## Quy chuẩn Code (Unity C#)
- **Naming convention**: Sử dụng `camelCase` cho biến cục bộ/ private fields, `PascalCase` cho class, method, và public properties.
- **Tối ưu năng lượng**: Không khởi tạo (`new`) object liên tục (đặc biệt là strings hay buffers) bên trong hàm `Update()` để tránh Garbage Collection Spike.
- Xử lý UI Thread: MediaPipe đôi lúc chạy trên luồng khác, mọi thay đổi UI (`.text`, `.style`) phải được đẩy lại main thread của Unity (Dispatcher).
