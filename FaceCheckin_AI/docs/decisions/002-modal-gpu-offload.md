# ADR-002: GPU Offload sang Modal

## Ngày: 2025-12

## Trạng thái: Accepted

## Bối cảnh
RetinaFace + ArcFace + YOLOv8 cần GPU để chạy nhanh. Cloud Run không hỗ trợ GPU (hoặc rất đắt).

## Quyết định
Offload toàn bộ AI inference sang **Modal** (GPU T4, serverless). Cloud Run chỉ làm API gateway + FAISS matching.

## Lý do
1. **Chi phí**: Modal tính per-second GPU usage (~$0.000164/s cho T4). Rẻ hơn nhiều so với Cloud Run GPU ($0.6/giờ)
2. **Cold start chấp nhận được**: 30-60s cho lần đầu, sau đó warm ~2-5s
3. **Tách biệt concern**: Backend không cần TensorFlow/PyTorch → image nhẹ (~200MB vs ~3GB)
4. **Scale tự động**: Modal auto-scale GPU workers theo demand

## Hệ quả
- Phụ thuộc vào Modal uptime
- Cold start 30-60s nếu worker ngủ quá 5 phút
- Cần monitor Modal billing riêng
