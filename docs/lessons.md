# 🧠 BÀI HỌC KINH NGHIỆM (LESSONS LEARNED)

Quy tắc cốt lõi: Không lặp lại lỗi cũ. Mọi lỗi lầm đều phải được chuyển hóa thành quy tắc (Rule).

## [2026-02-24] - Lỗi tràn bộ nhớ khi xử lý luồng Video (Memory Leak)
- **Vấn đề:** Ứng dụng bị crash sau khoảng 10 phút chạy liên tục khi xử lý khung hình từ camera.
- **Nguyên nhân gốc rễ (Root Cause):** Khởi tạo object nhận diện bên trong vòng lặp `while True` đọc frame, khiến rác bộ nhớ (garbage) sinh ra liên tục mà không kịp dọn dẹp.
- **Quy tắc mới (New Rule):** 👉 TUYỆT ĐỐI KHÔNG khởi tạo các model nặng (như detector, predictor) bên trong vòng lặp xử lý frame. Phải khởi tạo một lần duy nhất ở cấp độ toàn cục (global) hoặc trước khi vào vòng lặp, và chỉ truyền frame vào để suy luận (inference).

## [2026-02-20] - Cold Start quá chậm trên Serverless (Cloud Run)
- **Vấn đề:** Lần gọi API đầu tiên mất tới 15 giây mới có phản hồi (Timeout).
- **Nguyên nhân gốc rễ (Root Cause):** Import toàn bộ thư viện nặng (ví dụ: `import tensorflow`, `import torch`) ở ngay đầu file, khiến container mất quá nhiều thời gian khởi động môi trường dù chưa có request thực tế.
- **Quy tắc mới (New Rule):** 👉 Sử dụng "Lazy Import". Chỉ import các thư viện nặng ở bên trong function (hàm) thực sự cần dùng đến chúng, để tối ưu thời gian khởi động ứng dụng.

## [2026-02-24] - Background Thread không chạy trên Cloud Run (Serverless)
- **Vấn đề:** Viết `while True` loop trong background thread để ping Modal mỗi 4 phút → Tưởng hoạt động nhưng thực tế thread bị "đóng băng" khi không có request.
- **Nguyên nhân gốc rễ (Root Cause):** Cloud Run mặc định dùng chế độ "CPU is only allocated during request processing". Khi không có request, CPU bị throttle → mọi background thread, timer, cron nội bộ đều **DỪNG CHẠY**.
- **Giải pháp đúng:** Dùng **external scheduler** (Cloud Scheduler, cron-job.org, GitHub Actions) gọi vào 1 endpoint HTTP. Không dựa vào background thread trên serverless.
- **Quy tắc mới (New Rule):** 👉 KHÔNG BAO GIỜ dùng `threading` + `while True` trên serverless (Cloud Run, Lambda, Cloud Functions). Nếu cần task định kỳ → luôn dùng external trigger (scheduler, webhook, message queue).

## [2026-02-25] - Hai hệ thống song song gây nhầm lẫn dữ liệu (Dead Code)
- **Vấn đề:** Google Sheets có 2 hệ thống (v1 dùng Service Account, v2 dùng Teacher OAuth) tồn tại song song trong cùng 1 file. Header khác nhau (v1: STT/HọTên/MSSV, v2: MSSV/HọTên) → nếu dùng lẫn sẽ ghi sai cột.
- **Nguyên nhân gốc rễ (Root Cause):** Khi phát triển v2, v1 không được xóa mà để lại "phòng hờ". Theo thời gian, v1 trở thành dead code (~666 dòng) nhưng vẫn expose route `/gsheets/bootstrap` và `/gsheets/sync_roster`.
- **Giải pháp:** Xóa toàn bộ v1 (22+ hàm, 2 routes, 3 biến global). Giữ nguyên v2 đã hoàn chỉnh hơn.
- **Quy tắc mới (New Rule):** 👉 Khi viết hệ thống thay thế (v2), PHẢI xóa hệ thống cũ (v1) ngay khi v2 đã ổn định. Không để dead code tồn tại song song — nó gây nhầm lẫn, tăng kích thước file, và có nguy cơ gọi nhầm API.

## [2026-03-27] - Lỗi "Invalid Token" do Clock Skew (Chênh lệch thời gian)
- **Vấn đề:** Ứng dụng báo lỗi "Invalid Token" liên tục sau khi deploy lên Cloud Run, mặc dù token được tạo đúng cách.
- **Nguyên nhân gốc rễ (Root Cause):** Chênh lệch thời gian giữa máy local (Windows) và server (Google Cloud) vượt quá ngưỡng cho phép của Google OAuth (thường là 60 giây). Máy local có thể chạy nhanh hơn hoặc chậm hơn server đáng kể.
- **Giải pháp:** Sử dụng thư viện `google-auth` với tham số `clock_skew_in_seconds=60` (hoặc cao hơn) khi khởi tạo credentials. Điều này cho phép thư viện tự động bù trừ sai lệch thời gian.
- **Quy tắc mới (New Rule):** 👉 Khi sử dụng Google OAuth trên serverless, luôn cấu hình `clock_skew_in_seconds` để tránh lỗi Invalid Token do chênh lệch múi giờ/đồng hồ.

## [2026-03-27] - Cloud Run gửi Firestore query tới project sai (Cross-Project Routing)
- **Vấn đề:** Sau khi deploy lên Cloud Run (GCP project: facecheckin-ai), ứng dụng báo lỗi Firestore không tìm thấy dữ liệu hoặc ghi dữ liệu vào project không đúng. Điều này xảy ra mặc dù `GOOGLE_APPLICATION_CREDENTIALS` đã được cấu hình đúng.
- **Nguyên nhân gốc rễ (Root Cause):** Cloud Run tự động set env var `GOOGLE_CLOUD_PROJECT` thành project ID của GCP project chứa container. Khi thư viện Firebase (`firebase_admin`, `google-cloud-firestore`) được import, chúng mặc định sử dụng `GOOGLE_CLOUD_PROJECT` nếu được set, thay vì lấy project ID từ service account JSON. Điều này khiến queries bị route sang project sai.
- **Giải pháp:** Override `GOOGLE_CLOUD_PROJECT` env var trước khi bất kỳ import Google Cloud nào được thực thi. Thiết lập nó thành project ID chính xác từ `serviceAccountKey.json`. Việc này phải được làm ở đầu file `config/settings.py` trước khi import bất kỳ module Firebase/GCP nào.
- **Code Fix:**
  ```python
  # config/settings.py
  import os
  import json

  # Đọc service account key để lấy project_id
  if os.path.exists('serviceAccountKey.json'):
      with open('serviceAccountKey.json', 'r') as f:
          sa_key = json.load(f)
          os.environ['GOOGLE_CLOUD_PROJECT'] = sa_key.get('project_id', 'phamgiahuy-9ab1c')

  # CHỈ SAU ĐÓ mới import Firebase/GCP modules
  import firebase_admin
  # ... rest of imports
  ```
- **Quy tắc mới (New Rule):** 👉 Trên serverless như Cloud Run, env var `GOOGLE_CLOUD_PROJECT` được tự động set. Luôn override nó thành project ID chính xác từ service account trước khi import bất kỳ Google Cloud library nào.
