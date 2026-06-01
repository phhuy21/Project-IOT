# API Layer Context

Tầng này chứa tất cả các định tuyến (routers) của Flask.
Mỗi file đại diện cho một Blueprint cụ thể (ví dụ: auth, verify, profiles).

Quy tắc:
1. KHÔNG viết core logic phức tạp hoặc gọi trực tiếp AI model ở đây.
2. Tất cả data access phức tạp nên thông qua helper hoặc service ở tầng `core/`.
3. LUÔN thêm decorator `@_require_api_key` cho mọi POST/DELETE request nhạy cảm.
4. Trả về chuẩn JSON format với `status` và `message`.
