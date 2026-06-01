# Core Logic Context

Tầng này chứa toàn bộ logic nghiệp vụ (business logic), thuật toán, và giao tiếp với các service bên ngoài (GPU, database).

Quy tắc:
1. Tách biệt hoàn toàn việc xử lý request HTTP (Flask) ra khỏi logic nội bộ.
2. Mọi kết nối đến AI worker (Modal) phải qua `modal_client.py`.
3. Logic phân vùng dữ liệu (multi-tenancy) dùng chung trong `multitenancy.py`.
4. Khi chỉnh sửa `faiss_engine.py`, phải đặc biệt chú ý đến memory management (không instantiate model trong loop).
