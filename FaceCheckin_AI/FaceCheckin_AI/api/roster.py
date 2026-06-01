"""
api/roster.py — Routes /roster, /roster/import, /schedule_bundle.
Được tách từ main.py dòng 1748-1881.
"""
from flask import Blueprint, request, jsonify
from firebase_admin import firestore

from config.settings import db, TEACHERS_COLLECTION
from core.multitenancy import (
    _norm, get_teacher_id_from_req, get_term_id_from_req,
    get_class_id_from_req, get_roster_collection
)
from core.helpers import _require_api_key

roster_bp = Blueprint('roster', __name__)


# =========================
# SCHEDULE BUNDLE (SYNC)
# =========================
def get_schedule_bundle_doc(teacher_id: str):
    if not teacher_id:
        return None
    return f"{TEACHERS_COLLECTION}/{teacher_id}/ScheduleBundle/main"


@roster_bp.route("/roster", methods=["GET"])
def roster_get():
    try:
        teacher_id = get_teacher_id_from_req()
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()
        col_path = get_roster_collection(teacher_id, term_id, class_id)

        if db is None:
            return jsonify({"status": "success", "students": []})

        docs = db.collection(col_path).stream()
        students = []
        for d in docs:
            x = d.to_dict() or {}
            students.append({
                "mssv": x.get("mssv", d.id),
                "name": x.get("name", ""),
                "registered": bool(x.get("registered", False))
            })

        students.sort(key=lambda s: s.get("mssv", ""))
        return jsonify({"status": "success", "students": students})
    except Exception as e:
        return jsonify({"error": str(e)}), 500


@roster_bp.route("/roster/import", methods=["POST"])
@_require_api_key
def roster_import():
    try:
        payload = request.json or {}
        arr = payload.get("students") or []
        if not isinstance(arr, list) or len(arr) == 0:
            return jsonify({"error": "Missing students[]"}), 400

        teacher_id = get_teacher_id_from_req()
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()
        col_path = get_roster_collection(teacher_id, term_id, class_id)

        if db is None:
            return jsonify({"status": "success", "count": len(arr)})

        batch = db.batch()
        ops = 0
        count = 0

        for s in arr:
            name = _norm(s.get("name"))
            mssv = _norm(s.get("mssv"))
            if not name or not mssv:
                continue

            doc_ref = db.collection(col_path).document(mssv)
            batch.set(doc_ref, {
                "name": name,
                "mssv": mssv,
                "registered": bool(s.get("registered", False)),
                "updated_at": firestore.SERVER_TIMESTAMP,
                "created_at": firestore.SERVER_TIMESTAMP,
            }, merge=True)

            ops += 1
            count += 1
            if ops >= 450:
                batch.commit()
                batch = db.batch()
                ops = 0

        if ops > 0:
            batch.commit()

        return jsonify({"status": "success", "count": count})
    except Exception as e:
        return jsonify({"error": str(e)}), 500


@roster_bp.route("/schedule_bundle", methods=["GET"])
def schedule_bundle_get():
    try:
        teacher_id = get_teacher_id_from_req()
        if not teacher_id:
            return jsonify({"status": "error", "message": "Missing X-Teacher-ID"}), 400
        if not db:
            return jsonify({"status": "success", "sessions": []}), 200

        doc_path = get_schedule_bundle_doc(teacher_id)
        snap = db.document(doc_path).get()
        if not snap.exists:
            return jsonify({"status": "success", "sessions": []}), 200

        data = snap.to_dict() or {}
        data["status"] = "success"
        return jsonify(data), 200
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500


@roster_bp.route("/schedule_bundle", methods=["POST"])
@_require_api_key
def schedule_bundle_set():
    try:
        teacher_id = get_teacher_id_from_req()
        if not teacher_id:
            return jsonify({"status": "error", "message": "Missing X-Teacher-ID"}), 400
        if not db:
            return jsonify({"status": "error", "message": "Firestore not initialized"}), 500

        body = request.json or {}

        allow = {
            "sessions",
            "schoolYear", "semester",
            "phase1Start", "phase1End", "phase2Start", "phase2End",
            "selectedClassCode", "selectedTermId"
        }
        payload = {k: body.get(k) for k in allow if k in body}
        payload["updated_at"] = firestore.SERVER_TIMESTAMP

        doc_path = get_schedule_bundle_doc(teacher_id)
        db.document(doc_path).set(payload, merge=True)

        return jsonify({"status": "success"}), 200
    except Exception as e:
        return jsonify({"status": "error", "message": str(e)}), 500
