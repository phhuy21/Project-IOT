# ADR-001: Chọn FAISS thay vì Cloud Vector Database

## Ngày: 2025-12

## Trạng thái: Accepted

## Bối cảnh
Cần lưu và tìm kiếm vector embedding 512 chiều (ArcFace) để nhận diện khuôn mặt. Các lựa chọn: FAISS (local), Pinecone (SaaS), Milvus (self-hosted), Qdrant.

## Quyết định
Sử dụng **FAISS IndexFlatIP** (in-memory, cosine similarity) trên Cloud Run.

## Lý do
1. **Chi phí**: FAISS miễn phí. Pinecone/Qdrant tốn $70+/tháng cho managed tier
2. **Latency**: FAISS in-memory ~0.5ms, vs Pinecone ~15-30ms (network round-trip)
3. **Scale phù hợp**: Mỗi lớp tối đa ~100 sinh viên × 5 vectors = 500 vectors/class → FAISS xử lý thoải mái
4. **Đơn giản**: Không cần quản lý thêm infra, không có network dependency

## Hệ quả
- Data phải reload từ Firestore mỗi khi Cloud Run cold start
- Không phù hợp nếu scale lên >10,000 sinh viên (cần xem xét lại)
- FAISS index chỉ tồn tại trong RAM, không persistent
