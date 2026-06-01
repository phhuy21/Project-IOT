"""
api/auth.py — Authentication routes.
Được tách từ main.py dòng 212-358.
"""
import time
import random
import smtplib
from email.mime.text import MIMEText

from flask import Blueprint, request, jsonify
from firebase_admin import firestore

from config.settings import (
    db, TEACHERS_COLLECTION, SMTP_EMAIL, SMTP_PASSWORD, SMTP_HOST, SMTP_PORT
)

auth_bp = Blueprint('auth', __name__)


# --- EMAIL HELPER ---
def send_email_otp(to_email, otp):
    if not SMTP_EMAIL or not SMTP_PASSWORD:
        print("⚠️ SMTP credentials not set. Skipping email.")
        return False

    try:
        msg = MIMEText(f"Mã xác nhận của bạn là: {otp}\nMã có hiệu lực trong 5 phút.")
        msg['Subject'] = "FaceCheckin - Mã xác nhận quên mật khẩu"
        msg['From'] = SMTP_EMAIL
        msg['To'] = to_email

        with smtplib.SMTP_SSL(SMTP_HOST, SMTP_PORT) as server:
            server.login(SMTP_EMAIL, SMTP_PASSWORD)
            server.sendmail(SMTP_EMAIL, to_email, msg.as_string())
        return True
    except Exception as e:
        print(f"Error sending email: {e}")
        return False


@auth_bp.route('/auth/register', methods=['POST'])
def auth_register():
    try:
        data = request.json or {}
        username = data.get("username", "").strip()
        password = data.get("password", "").strip()
        email = data.get("email", "").strip()
        
        if not username or not password:
            return jsonify({"error": "Missing info"}), 400
            
        doc_ref = db.collection(TEACHERS_COLLECTION).document(username)
        if doc_ref.get().exists:
            return jsonify({"error": "Account exists"}), 409
            
        doc_ref.set({
            "username": username,
            "password": password, 
            "email": email,
            "created_at": firestore.SERVER_TIMESTAMP
        })
        return jsonify({"status": "success", "message": f"Welcome {username}"}), 200
    except Exception as e:
        return jsonify({"error": str(e)}), 500


@auth_bp.route('/auth/login', methods=['POST'])
def auth_login():
    try:
        data = request.json or {}
        username = data.get("username", "").strip()
        password = data.get("password", "").strip()
        
        doc_ref = db.collection(TEACHERS_COLLECTION).document(username)
        snap = doc_ref.get()
        if not snap.exists:
             return jsonify({"error": "User not found"}), 404
             
        user_data = snap.to_dict()
        if user_data.get("password") == password:
             return jsonify({
                 "status": "success", 
                 "teacher_id": username,
                 "message": "Login successful"
             }), 200
        else:
             return jsonify({"error": "Wrong password"}), 401
    except Exception as e:
        return jsonify({"error": str(e)}), 500


@auth_bp.route('/auth/forgot-password', methods=['POST'])
def forgot_password():
    data = request.json
    email = data.get('email')
    
    # Check if email exists
    users = db.collection(TEACHERS_COLLECTION).where('email', '==', email).stream()
    user_doc = None
    for u in users:
        user_doc = u
        break
        
    if not user_doc:
        return jsonify({"status": "error", "message": "Email không tồn tại!"}), 404
        
    otp = str(random.randint(100000, 999999))
    
    db.collection('PasswordResets').document(email).set({
        "otp": otp,
        "expires_at": int(time.time()) + 300
    })
    
    if send_email_otp(email, otp):
        return jsonify({"status": "success", "message": "OTP sent"})
    else:
        return jsonify({"status": "error", "message": "Failed to send email"}), 500


@auth_bp.route('/auth/reset-password', methods=['POST'])
def reset_password():
    data = request.json
    email = data.get('email')
    otp = data.get('otp')
    new_password = data.get('new_password')
    
    reset_doc = db.collection('PasswordResets').document(email).get()
    if not reset_doc.exists:
        return jsonify({"status": "error", "message": "Yêu cầu không hợp lệ"}), 400
        
    reset_data = reset_doc.to_dict()
    if reset_data['otp'] != otp:
        return jsonify({"status": "error", "message": "Mã OTP sai"}), 400
        
    if int(time.time()) > reset_data['expires_at']:
        return jsonify({"status": "error", "message": "Mã OTP đã hết hạn"}), 400
        
    users = db.collection(TEACHERS_COLLECTION).where('email', '==', email).stream()
    for u in users:
        u.reference.update({"password": new_password})
        
    db.collection('PasswordResets').document(email).delete()
    
    return jsonify({"status": "success", "message": "Reset done"})


@auth_bp.route('/auth/change-password', methods=['POST'])
def change_password():
    try:
        data = request.json
        username = data.get('username')
        old_password = data.get('old_password')
        new_password = data.get('new_password')
        
        if not username or not old_password or not new_password:
             return jsonify({"status": "error", "message": "Thiếu thông tin"}), 400

        doc_ref = db.collection(TEACHERS_COLLECTION).document(username)
        snap = doc_ref.get()
        
        if not snap.exists:
            return jsonify({"status": "error", "message": "Tài khoản không tồn tại"}), 404
            
        user_data = snap.to_dict()
        if user_data.get("password") != old_password:
            return jsonify({"status": "error", "message": "Mật khẩu cũ không đúng"}), 400
            
        doc_ref.update({"password": new_password})
        return jsonify({"status": "success", "message": "Đổi mật khẩu thành công"}), 200
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500
