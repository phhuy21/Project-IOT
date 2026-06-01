"""
main.py — Entry Point cho FaceCheckin AI Backend.
Đã được refactor: Tất cả logic tách thành modules trong config/, core/, api/.
File gốc backup tại main_backup.py
"""
import os
import threading

# --- IMPORT CONFIG (phải import đầu tiên để init Firebase + Flask app) ---
from config.settings import app, WARMUP_ON_START, FAISS_METRIC, NORMALIZE_EMBEDDINGS

# --- IMPORT CORE (cần thiết cho warmup) ---
from core.faiss_engine import load_teacher_data

# --- IMPORT & REGISTER BLUEPRINTS ---
from api.auth import auth_bp
from api.verify import verify_bp
from api.register import register_bp
from api.profiles import profiles_bp
from api.roster import roster_bp
from api.gsheets import gsheets_bp

app.register_blueprint(auth_bp)
app.register_blueprint(verify_bp)
app.register_blueprint(register_bp)
app.register_blueprint(profiles_bp)
app.register_blueprint(roster_bp)
app.register_blueprint(gsheets_bp)


# --- HEALTH CHECK (giữ ở entry point) ---
from flask import jsonify
import time

@app.route('/', methods=['GET'])
def health_check():
    return jsonify({
        "status": "ok",
        "message": "Face API is running!",
        "timestamp": int(time.time())
    }), 200


# --- WARMUP ---
def warmup():
    print("🔥 Warmup Thread Started...")
    print("🔥 Warmup Finished.")


if __name__ == "__main__":
    if WARMUP_ON_START:
        threading.Thread(target=warmup, daemon=True).start()
    else:
        load_teacher_data(None)

    port = int(os.getenv("PORT", "5050"))
    print(f"🚀 Server running on port {port} | Metric: {FAISS_METRIC} | Norm: {NORMALIZE_EMBEDDINGS}")
    app.run(host="0.0.0.0", port=port, debug=False)