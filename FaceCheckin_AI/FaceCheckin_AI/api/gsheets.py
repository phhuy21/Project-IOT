"""
api/gsheets.py — Google OAuth + Google Sheets v2 + Backfill.
Được tách từ main.py dòng 1883-2539.
"""
import os
import re
import time
import uuid
import traceback
from datetime import datetime, timedelta

from flask import Blueprint, request, jsonify
from firebase_admin import firestore
from google.oauth2.credentials import Credentials
from google_auth_oauthlib.flow import Flow
from google.auth.transport.requests import Request as GoogleRequest
from googleapiclient.discovery import build
from googleapiclient.errors import HttpError

from config.settings import (
    db, bucket, TEACHERS_COLLECTION,
    GOOGLE_CLIENT_ID, GOOGLE_CLIENT_SECRET, GOOGLE_REDIRECT_URI, GOOGLE_SCOPES
)
from core.multitenancy import (
    get_teacher_id_from_req, get_term_id_from_req, get_class_id_from_req,
    get_class_root, get_face_collection, get_roster_collection
)
from core.helpers import _require_api_key

gsheets_bp = Blueprint('gsheets', __name__)


# --- HELPER FUNCTIONS ---

def _now_epoch():
    return int(time.time())

def _safe(s, default=""):
    s = (s or "").strip()
    return s if s else default

def _parse_vn_date(s):
    if not s: return None
    try:
        return datetime.strptime(s.strip(), "%d/%m/%Y").date()
    except:
        return None

def _parse_iso_date(s):
    if not s: return None
    try:
        return datetime.strptime(s.strip(), "%Y-%m-%d").date()
    except:
        return None

def _monday_of_week(d):
    return d - timedelta(days=d.weekday())

def _phase_week_for_date(d, cfg: dict):
    p1s = _parse_vn_date(cfg.get("phase1Start"))
    p2s = _parse_vn_date(cfg.get("phase2Start"))
    p1e = _parse_vn_date(cfg.get("phase1End"))
    p2e = _parse_vn_date(cfg.get("phase2End"))
    if p1s:
        m1 = _monday_of_week(p1s)
        w = ((d - m1).days // 7) + 1
        if (p1e and d <= p1e) or (not p1e and w >= 1):
            w = max(1, min(9, w))
            return 1, w
    if p2s:
        m2 = _monday_of_week(p2s)
        w = ((d - m2).days // 7) + 1
        if (p2e and d <= p2e) or (not p2e and w >= 1):
            w = max(1, min(9, w))
            return 2, w
    return 1, 1

def _a1_col(n1):
    s = ""
    n = int(n1)
    while n > 0:
        n, r = divmod(n - 1, 26)
        s = chr(65 + r) + s
    return s

def _attendance_col(phase: int, week_no: int):
    phase = 1 if phase != 2 else 2
    week_no = max(1, min(9, int(week_no)))
    if phase == 1:
        col_idx = 3 + (week_no - 1)
    else:
        col_idx = 3 + 9 + (week_no - 1)
    return _a1_col(col_idx)

def _sanitize_sheet_title(title: str):
    t = (title or "").strip()
    t = re.sub(r"[:\\/?*\[\]]", "-", t)
    t = re.sub(r"\s+", " ", t).strip()
    if not t:
        t = "Mon"
    if len(t) > 95:
        t = t[:95].rstrip()
    return t

def _sheet_title_for_subject(subject_code: str, subject_name: str):
    code = _safe(subject_code)
    name = _safe(subject_name)
    if name and code:
        return _sanitize_sheet_title(f"{name} ({code})")
    if code:
        return _sanitize_sheet_title(code)
    return _sanitize_sheet_title(name or "Mon")


# --- FIRESTORE DOC PATHS ---

def _pending_doc(poll_token: str):
    return f"GoogleOAuthPending/{poll_token}"

def _teacher_oauth_doc(teacher_id: str):
    return f"{TEACHERS_COLLECTION}/{teacher_id}/GoogleOAuth/main"

def _tenant_gsheet_doc(teacher_id, term_id, class_id):
    root = get_class_root(teacher_id, term_id, class_id)
    if root:
        return f"{root}/gsheets/main"
    return f"{TEACHERS_COLLECTION}/{teacher_id}/gsheets/main"

def _tenant_gsheet_cfg_doc(teacher_id, term_id, class_id):
    root = get_class_root(teacher_id, term_id, class_id)
    if root:
        return f"{root}/gsheets/config"
    return f"{TEACHERS_COLLECTION}/{teacher_id}/gsheets/config"

def _tenant_gsheet_synced_doc(teacher_id, term_id, class_id, session_key: str):
    root = get_class_root(teacher_id, term_id, class_id)
    sid = re.sub(r"[^a-zA-Z0-9_\-]", "_", session_key or "session")
    if root:
        return f"{root}/gsheets/main/synced_sessions/{sid}"
    return f"{TEACHERS_COLLECTION}/{teacher_id}/gsheets/main/synced_sessions/{sid}"


# --- GOOGLE AUTH HELPERS ---

def _google_client_config():
    if not GOOGLE_CLIENT_ID or not GOOGLE_CLIENT_SECRET or not GOOGLE_REDIRECT_URI:
        return None
    return {
        "web": {
            "client_id": GOOGLE_CLIENT_ID,
            "client_secret": GOOGLE_CLIENT_SECRET,
            "auth_uri": "https://accounts.google.com/o/oauth2/auth",
            "token_uri": "https://oauth2.googleapis.com/token",
            "redirect_uris": [GOOGLE_REDIRECT_URI],
        }
    }

def _get_google_creds(teacher_id: str):
    if not db:
        return None, "Firestore not initialized"
    doc = db.document(_teacher_oauth_doc(teacher_id)).get()
    if not doc.exists:
        return None, "No Google OAuth token"
    data = doc.to_dict() or {}
    refresh_token = data.get("refresh_token")
    if not refresh_token:
        return None, "No refresh_token"
    creds = Credentials(
        token=data.get("token"),
        refresh_token=refresh_token,
        token_uri=data.get("token_uri") or "https://oauth2.googleapis.com/token",
        client_id=data.get("client_id") or GOOGLE_CLIENT_ID,
        client_secret=data.get("client_secret") or GOOGLE_CLIENT_SECRET,
        scopes=data.get("scopes") or GOOGLE_SCOPES,
    )
    try:
        if creds.expired and creds.refresh_token:
            creds.refresh(GoogleRequest())
            db.document(_teacher_oauth_doc(teacher_id)).set({
                "token": creds.token,
                "updated_at": firestore.SERVER_TIMESTAMP,
            }, merge=True)
    except Exception as e:
        return None, f"Refresh token failed: {e}"
    return creds, None

def _sheets_service(creds: Credentials):
    return build("sheets", "v4", credentials=creds, cache_discovery=False)

def _oauth2_service(creds: Credentials):
    return build("oauth2", "v2", credentials=creds, cache_discovery=False)


# --- SPREADSHEET HELPERS ---

def _ensure_spreadsheet_v2(teacher_id, term_id, class_id):
    if not db:
        raise RuntimeError("Firestore not initialized")
    term_id = _safe(term_id, "default_term")
    class_id = _safe(class_id, "default_class")
    creds, err = _get_google_creds(teacher_id)
    if err:
        return None, None, False, err
    doc_path = _tenant_gsheet_doc(teacher_id, term_id, class_id)
    snap = db.document(doc_path).get()
    if snap.exists:
        meta = snap.to_dict() or {}
        sid = meta.get("spreadsheet_id")
        surl = meta.get("spreadsheet_url")
        if sid and surl:
            return sid, surl, False, None
    svc = _sheets_service(creds)
    title = f"FaceCheckin - {teacher_id} - {term_id} - {class_id}"
    body = {"properties": {"title": title}}
    resp = svc.spreadsheets().create(body=body, fields="spreadsheetId,spreadsheetUrl").execute()
    sid = resp.get("spreadsheetId")
    surl = resp.get("spreadsheetUrl")
    db.document(doc_path).set({
        "spreadsheet_id": sid,
        "spreadsheet_url": surl,
        "teacher_id": teacher_id,
        "term_id": term_id,
        "class_id": class_id,
        "created_at": firestore.SERVER_TIMESTAMP,
        "updated_at": firestore.SERVER_TIMESTAMP,
    }, merge=True)
    return sid, surl, True, None

def _get_spreadsheet_sheets_v2(svc, spreadsheet_id):
    meta = svc.spreadsheets().get(spreadsheetId=spreadsheet_id).execute()
    sheets = meta.get("sheets", []) or []
    title_to_id = {}
    for sh in sheets:
        props = (sh.get("properties") or {})
        title_to_id[props.get("title")] = props.get("sheetId")
    return title_to_id

def _ensure_subject_tab_header_v2(svc, spreadsheet_id, tab_title: str):
    tab_title = _sanitize_sheet_title(tab_title)
    title_to_id = _get_spreadsheet_sheets_v2(svc, spreadsheet_id)
    requests_list = []
    if tab_title not in title_to_id:
        requests_list.append({
            "addSheet": {"properties": {"title": tab_title, "gridProperties": {"rowCount": 1000, "columnCount": 30}}}
        })
    if requests_list:
        svc.spreadsheets().batchUpdate(spreadsheetId=spreadsheet_id, body={"requests": requests_list}).execute()
    headers = ["MSSV", "Họ và tên"] + [f"Đ1-T{i}" for i in range(1, 10)] + [f"Đ2-T{i}" for i in range(1, 10)]
    last_col = _a1_col(len(headers))
    rng = f"'{tab_title}'!A1:{last_col}1"
    svc.spreadsheets().values().update(spreadsheetId=spreadsheet_id, range=rng, valueInputOption="RAW", body={"values": [headers]}).execute()
    try:
        title_to_id = _get_spreadsheet_sheets_v2(svc, spreadsheet_id)
        sheet_id = title_to_id.get(tab_title)
        if sheet_id is not None:
            svc.spreadsheets().batchUpdate(spreadsheetId=spreadsheet_id, body={"requests": [{"updateSheetProperties": {"properties": {"sheetId": sheet_id, "gridProperties": {"frozenRowCount": 1}}, "fields": "gridProperties.frozenRowCount"}}]}).execute()
    except Exception as e:
        print(f"[GSheets v2] Error freezing header row: {e}")
    return tab_title

def _sheet_row_map_v2(svc, spreadsheet_id, tab_title, max_rows=2000):
    rng = f"'{tab_title}'!A2:B{max_rows}"
    resp = svc.spreadsheets().values().get(spreadsheetId=spreadsheet_id, range=rng).execute()
    values = resp.get("values") or []
    row_map = {}
    last_row = 1
    for i, row in enumerate(values):
        mssv = _safe(row[0] if len(row) > 0 else "")
        if mssv:
            row_idx = 2 + i
            row_map[mssv] = row_idx
            last_row = row_idx
    return row_map, last_row

def _apply_attendance_updates_v2(svc, spreadsheet_id, tab_title, col_letter, rows):
    roster_updates = 0
    write_updates = []
    row_map, last_row = _sheet_row_map_v2(svc, spreadsheet_id, tab_title)
    next_row = last_row + 1
    for it in (rows or []):
        mssv = _safe(it.get("mssv"))
        name = _safe(it.get("name"))
        status = _safe(it.get("status"), "A").upper()
        if status not in ("P", "L", "A"):
            status = "A"
        if not mssv:
            continue
        if mssv in row_map:
            r = row_map[mssv]
            write_updates.append({"range": f"'{tab_title}'!B{r}", "values": [[name]]})
        else:
            r = next_row
            next_row += 1
            row_map[mssv] = r
            write_updates.append({"range": f"'{tab_title}'!A{r}:B{r}", "values": [[mssv, name]]})
            roster_updates += 1
        write_updates.append({"range": f"'{tab_title}'!{col_letter}{r}", "values": [[status]]})
    if write_updates:
        svc.spreadsheets().values().batchUpdate(spreadsheetId=spreadsheet_id, body={"valueInputOption": "RAW", "data": write_updates}).execute()
    return {"rows_written": len(rows or []), "cells_written": len(write_updates), "new_roster_rows": roster_updates}


# --- OAUTH ROUTES ---

@gsheets_bp.route("/google/oauth/start", methods=["GET"])
def google_oauth_start():
    try:
        cfg = _google_client_config()
        if not cfg:
            return jsonify({"error": "Missing GOOGLE_CLIENT_ID/SECRET/REDIRECT_URI"}), 500
        teacher_id = get_teacher_id_from_req()
        if not teacher_id:
            return jsonify({"error": "Missing X-Teacher-ID"}), 400
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()
        poll_token = uuid.uuid4().hex
        flow = Flow.from_client_config(cfg, scopes=GOOGLE_SCOPES)
        flow.redirect_uri = GOOGLE_REDIRECT_URI
        auth_url, _ = flow.authorization_url(access_type="offline", include_granted_scopes="true", prompt="consent", state=poll_token)
        if not db:
            return jsonify({"error": "Firestore not initialized"}), 500
        db.document(_pending_doc(poll_token)).set({"flow": "connect", "teacher_id": teacher_id, "term_id": term_id, "class_id": class_id, "code_verifier": flow.code_verifier, "status": "pending", "created_at": firestore.SERVER_TIMESTAMP, "expires_at": _now_epoch() + 600}, merge=True)
        return jsonify({"authorization_url": auth_url, "poll_token": poll_token}), 200
    except Exception as e:
        return jsonify({"error": str(e)}), 500


@gsheets_bp.route("/google/oauth/status", methods=["GET"])
def google_oauth_status():
    try:
        teacher_id = get_teacher_id_from_req()
        if not teacher_id:
            return jsonify({"connected": False, "has_refresh_token": False}), 200
        if not db:
            return jsonify({"connected": False, "has_refresh_token": False}), 200
        snap = db.document(_teacher_oauth_doc(teacher_id)).get()
        if not snap.exists:
            return jsonify({"connected": False, "has_refresh_token": False}), 200
        data = snap.to_dict() or {}
        has_rt = bool(data.get("refresh_token"))
        return jsonify({"connected": has_rt, "has_refresh_token": has_rt, "email": data.get("email")}), 200
    except Exception as e:
        return jsonify({"connected": False, "has_refresh_token": False, "error": str(e)}), 200


@gsheets_bp.route("/google/oauth/callback", methods=["GET"])
def google_oauth_callback():
    try:
        cfg = _google_client_config()
        if not cfg:
            return "Missing GOOGLE_CLIENT_ID/SECRET/REDIRECT_URI", 500
        code = request.args.get("code")
        state = request.args.get("state")
        err = request.args.get("error")
        if err:
            return f"OAuth error: {err}", 400
        if not code or not state:
            return "Missing code/state", 400
        if not db:
            return "Firestore not initialized", 500
        pend_ref = db.document(_pending_doc(state))
        pend = pend_ref.get()
        if not pend.exists:
            return "Invalid/expired state", 400
        pend_data = pend.to_dict() or {}
        if pend_data.get("expires_at", 0) < _now_epoch():
            return "State expired", 400
        flow = Flow.from_client_config(cfg, scopes=GOOGLE_SCOPES, state=state)
        flow.redirect_uri = GOOGLE_REDIRECT_URI
        flow.code_verifier = pend_data.get("code_verifier")
        flow.fetch_token(code=code)
        creds = flow.credentials
        oauth = _oauth2_service(creds)
        me = oauth.userinfo().get().execute()
        email = me.get("email")
        flow_type = pend_data.get("flow") or "connect"
        if flow_type == "connect":
            teacher_id = pend_data.get("teacher_id")
            if not teacher_id:
                return "Missing teacher_id in pending", 400
            db.document(_teacher_oauth_doc(teacher_id)).set({"email": email, "token": creds.token, "refresh_token": creds.refresh_token or pend_data.get("refresh_token"), "token_uri": creds.token_uri, "client_id": GOOGLE_CLIENT_ID, "client_secret": GOOGLE_CLIENT_SECRET, "scopes": list(creds.scopes) if creds.scopes else GOOGLE_SCOPES, "updated_at": firestore.SERVER_TIMESTAMP}, merge=True)
            pend_ref.set({"status": "done", "email": email, "has_refresh_token": bool(creds.refresh_token), "updated_at": firestore.SERVER_TIMESTAMP}, merge=True)
            return "✅ FaceCheckin: Kết nối Google thành công. Bạn có thể đóng tab này.", 200
        _complete_login_flow_if_needed(state, creds, email)
        return "✅ FaceCheckin: Đăng nhập Google thành công. Bạn có thể đóng tab này.", 200
    except Exception as e:
        return f"Callback error: {e}", 500


@gsheets_bp.route("/auth/google/start", methods=["GET"])
def auth_google_start():
    try:
        cfg = _google_client_config()
        if not cfg:
            return jsonify({"error": "Missing GOOGLE_CLIENT_ID/SECRET/REDIRECT_URI"}), 500
        poll_token = uuid.uuid4().hex
        flow = Flow.from_client_config(cfg, scopes=GOOGLE_SCOPES)
        flow.redirect_uri = GOOGLE_REDIRECT_URI
        auth_url, _ = flow.authorization_url(access_type="offline", include_granted_scopes="true", prompt="consent", state=poll_token)
        if not db:
            return jsonify({"error": "Firestore not initialized"}), 500
        db.document(_pending_doc(poll_token)).set({"flow": "login", "code_verifier": flow.code_verifier, "status": "pending", "created_at": firestore.SERVER_TIMESTAMP, "expires_at": _now_epoch() + 600}, merge=True)
        return jsonify({"authorization_url": auth_url, "poll_token": poll_token}), 200
    except Exception as e:
        return jsonify({"error": str(e)}), 500


@gsheets_bp.route("/auth/google/poll", methods=["GET"])
def auth_google_poll():
    try:
        poll_token = request.args.get("p") or request.args.get("poll_token") or ""
        poll_token = poll_token.strip()
        if not poll_token:
            return jsonify({"status": "error", "error": "Missing poll_token"}), 400
        if not db:
            return jsonify({"status": "error", "error": "Firestore not initialized"}), 500
        snap = db.document(_pending_doc(poll_token)).get()
        if not snap.exists:
            return jsonify({"status": "error", "error": "Invalid poll_token"}), 404
        data = snap.to_dict() or {}
        st = data.get("status", "pending")
        return jsonify({"status": st, "teacher_id": data.get("teacher_id"), "email": data.get("email"), "has_refresh_token": bool(data.get("has_refresh_token")), "message": data.get("message"), "error": data.get("error")}), 200
    except Exception as e:
        return jsonify({"status": "error", "error": str(e)}), 500


@gsheets_bp.route("/auth/google/callback", methods=["GET"])
def auth_google_callback():
    return google_oauth_callback()


def _complete_login_flow_if_needed(state: str, creds, email: str):
    if not db:
        return
    pend_ref = db.document(_pending_doc(state))
    pend = pend_ref.get()
    if not pend.exists:
        return
    pend_data = pend.to_dict() or {}
    if (pend_data.get("flow") or "") != "login":
        return
    teacher_id = (email or "").strip().lower()
    if not teacher_id:
        pend_ref.set({"status": "error", "error": "No email"}, merge=True)
        return
    t_ref = db.collection(TEACHERS_COLLECTION).document(teacher_id)
    if not t_ref.get().exists:
        t_ref.set({"username": teacher_id, "password": "", "email": email, "created_at": firestore.SERVER_TIMESTAMP}, merge=True)
    db.document(_teacher_oauth_doc(teacher_id)).set({"email": email, "token": creds.token, "refresh_token": creds.refresh_token, "token_uri": creds.token_uri, "client_id": GOOGLE_CLIENT_ID, "client_secret": GOOGLE_CLIENT_SECRET, "scopes": list(creds.scopes) if creds.scopes else GOOGLE_SCOPES, "updated_at": firestore.SERVER_TIMESTAMP}, merge=True)
    pend_ref.set({"status": "done", "teacher_id": teacher_id, "email": email, "has_refresh_token": bool(creds.refresh_token), "message": "Login OK", "updated_at": firestore.SERVER_TIMESTAMP}, merge=True)


# --- GOOGLE SHEETS v2 ENDPOINTS ---

@gsheets_bp.route("/gsheets/ensure_v2", methods=["GET"])
@_require_api_key
def gsheets_ensure_v2():
    try:
        teacher_id = get_teacher_id_from_req()
        if not teacher_id:
            return jsonify({"ok": False, "need_google_auth": True, "message": "Missing X-Teacher-ID"}), 400
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()
        sid, surl, created, err = _ensure_spreadsheet_v2(teacher_id, term_id, class_id)
        if err:
            return jsonify({"ok": False, "status": "need_google_auth", "need_google_auth": True, "message": err, "spreadsheet_url": ""}), 200
        return jsonify({"ok": True, "status": "success", "url": surl, "spreadsheet_url": surl, "spreadsheet_id": sid, "created": created}), 200
    except Exception as e:
        return jsonify({"ok": False, "message": str(e)}), 500


@gsheets_bp.route("/gsheets/update_week", methods=["POST"])
@_require_api_key
def gsheets_update_week():
    try:
        teacher_id = get_teacher_id_from_req()
        if not teacher_id:
            return jsonify({"ok": False, "message": "Missing X-Teacher-ID"}), 400
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()

        body = request.json or {}
        session_key = _safe(body.get("sessionKey"))
        subject_code = _safe(body.get("subjectCode"))
        subject_name = _safe(body.get("subjectName"))
        session_date = _parse_iso_date(body.get("sessionDate"))

        if not session_key or not subject_code or not session_date:
            return jsonify({"ok": False, "message": "Missing sessionKey/subjectCode/sessionDate"}), 400

        if db:
             synced_ref = db.document(_tenant_gsheet_synced_doc(teacher_id, term_id, class_id, session_key))
             if synced_ref.get().exists:
                 meta = db.document(_tenant_gsheet_doc(teacher_id, term_id, class_id)).get()
                 surl = (meta.to_dict() or {}).get("spreadsheet_url", "") if meta.exists else ""
                 return jsonify({"ok": True, "status": "success", "already_synced": True, "spreadsheet_url": surl}), 200

        sid, surl, created, err = _ensure_spreadsheet_v2(teacher_id, term_id, class_id)
        if err:
            return jsonify({"ok": False, "status": "need_google_auth", "message": err, "spreadsheet_url": ""}), 200

        creds, cerr = _get_google_creds(teacher_id)
        if cerr:
            return jsonify({"ok": False, "message": cerr}), 200
        svc = _sheets_service(creds)

        cfg = {}
        if db:
            cfg_snap = db.document(_tenant_gsheet_cfg_doc(teacher_id, term_id, class_id)).get()
            if cfg_snap.exists:
                cfg = cfg_snap.to_dict() or {}

        phase, week_no = _phase_week_for_date(session_date, cfg or {})
        col = _attendance_col(phase, week_no)

        tab = _sheet_title_for_subject(subject_code, subject_name)
        tab = _ensure_subject_tab_header_v2(svc, sid, tab)

        rows = body.get("rows") or []
        result = _apply_attendance_updates_v2(svc, sid, tab, col, rows)

        if db:
            db.document(_tenant_gsheet_synced_doc(teacher_id, term_id, class_id, session_key)).set({
                "sessionKey": session_key,
                "subjectCode": subject_code,
                "subjectName": subject_name,
                "sessionDate": session_date.strftime("%Y-%m-%d"),
                "phase": phase,
                "week": week_no,
                "col": col,
                "updated_at": firestore.SERVER_TIMESTAMP,
                "created_at": firestore.SERVER_TIMESTAMP,
            }, merge=True)

        return jsonify({
            "ok": True,
            "status": "success",
            "spreadsheet_url": surl,
            "phase": phase,
            "week": week_no,
            "col": col,
            "result": result
        }), 200

    except Exception as e:
        return jsonify({"ok": False, "status": "error", "message": str(e), "error": str(e)}), 500


@gsheets_bp.route("/gsheets/finalize_v2", methods=["POST"])
@_require_api_key
def gsheets_finalize_v2():
    try:
        teacher_id = get_teacher_id_from_req()
        if not teacher_id:
            return jsonify({"ok": False, "message": "Missing X-Teacher-ID"}), 400
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()
        body = request.json or {}
        week = int(body.get("week") or 1)
        sheet_title = _sanitize_sheet_title(body.get("sheet_title") or "")
        updates = body.get("updates") or []
        if not sheet_title:
            return jsonify({"ok": False, "message": "Missing sheet_title"}), 400
        if week <= 9:
            phase, week_no = 1, week
        else:
            phase, week_no = 2, max(1, min(9, week - 9))
        col = _attendance_col(phase, week_no)
        sid, surl, created, err = _ensure_spreadsheet_v2(teacher_id, term_id, class_id)
        if err:
            return jsonify({"ok": False, "message": err}), 200
        creds, cerr = _get_google_creds(teacher_id)
        if cerr:
            return jsonify({"ok": False, "message": cerr}), 200
        svc = _sheets_service(creds)
        sheet_title = _ensure_subject_tab_header_v2(svc, sid, sheet_title)
        rows = []
        for u in updates:
            mssv = _safe(u.get("mssv"))
            name = _safe(u.get("name"))
            val = _safe(u.get("value"), "A").upper()
            if val not in ("P", "L", "A"):
                val = "A"
            if mssv:
                rows.append({"mssv": mssv, "name": name, "status": val})
        result = _apply_attendance_updates_v2(svc, sid, sheet_title, col, rows)
        updated = int((result or {}).get("rows_written", 0))
        return jsonify({"ok": True, "status": "success", "spreadsheet_url": surl, "phase": phase, "week": week_no, "col": col, "result": result, "updated": updated, "message": "Finalized"}), 200
    except Exception as e:
        return jsonify({"ok": False, "status": "error", "message": str(e), "error": str(e)}), 500


# --- BOOTSTRAP: Đẩy danh sách sinh viên + tạo tab môn học vào Sheet ---

@gsheets_bp.route("/gsheets/bootstrap", methods=["POST"])
@_require_api_key
def gsheets_bootstrap():
    """
    Tạo spreadsheet (nếu chưa có), tạo tab cho mỗi môn học trong schedule,
    điền header + danh sách sinh viên (MSSV, Họ và tên) từ roster Firestore.
    """
    try:
        teacher_id = get_teacher_id_from_req()
        if not teacher_id:
            return jsonify({"ok": False, "message": "Missing X-Teacher-ID"}), 400
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()

        # 1. Ensure spreadsheet tồn tại
        sid, surl, created, err = _ensure_spreadsheet_v2(teacher_id, term_id, class_id)
        if err:
            return jsonify({"ok": False, "status": "need_google_auth", "message": err}), 200

        creds, cerr = _get_google_creds(teacher_id)
        if cerr:
            return jsonify({"ok": False, "message": cerr}), 200
        svc = _sheets_service(creds)

        # 2. Lấy danh sách sinh viên từ roster
        roster_col = get_roster_collection(teacher_id, term_id, class_id)
        students = []
        if db:
            for d in db.collection(roster_col).stream():
                x = d.to_dict() or {}
                mssv = _safe(x.get("mssv", d.id))
                name = _safe(x.get("name", ""))
                if mssv:
                    students.append({"mssv": mssv, "name": name})
        students.sort(key=lambda s: s["mssv"])

        # 3. Lấy danh sách môn học từ schedule_bundle hoặc body request
        body = request.json or {}
        subjects = body.get("subjects") or []

        # Nếu client không gửi subjects, lấy từ ScheduleBundle trong Firestore
        if not subjects and db:
            bundle_path = f"{TEACHERS_COLLECTION}/{teacher_id}/ScheduleBundle/main"
            snap = db.document(bundle_path).get()
            if snap.exists:
                data = snap.to_dict() or {}
                sessions = data.get("sessions") or []
                seen = set()
                for s in sessions:
                    code = _safe(s.get("subjectCode"))
                    name = _safe(s.get("subjectName"))
                    if code and code not in seen:
                        seen.add(code)
                        subjects.append({"subjectCode": code, "subjectName": name})

        # Nếu vẫn không có subjects, tạo 1 tab mặc định
        if not subjects:
            subjects = [{"subjectCode": "General", "subjectName": "Điểm danh"}]

        # 4. Tạo tab + header + điền roster cho mỗi môn
        tabs_created = []
        for subj in subjects:
            code = _safe(subj.get("subjectCode"))
            name = _safe(subj.get("subjectName"))
            tab_title = _sheet_title_for_subject(code, name)
            tab_title = _ensure_subject_tab_header_v2(svc, sid, tab_title)

            # Điền roster nếu có sinh viên
            if students:
                row_map, last_row = _sheet_row_map_v2(svc, sid, tab_title)
                next_row = last_row + 1
                write_updates = []
                for st in students:
                    mssv = st["mssv"]
                    if mssv not in row_map:
                        write_updates.append({
                            "range": f"'{tab_title}'!A{next_row}:B{next_row}",
                            "values": [[mssv, st["name"]]]
                        })
                        row_map[mssv] = next_row
                        next_row += 1
                if write_updates:
                    svc.spreadsheets().values().batchUpdate(
                        spreadsheetId=sid,
                        body={"valueInputOption": "RAW", "data": write_updates}
                    ).execute()

            tabs_created.append(tab_title)

        # 5. Xóa tab "Sheet1" mặc định nếu có tab môn học
        if len(tabs_created) > 0:
            try:
                title_to_id = _get_spreadsheet_sheets_v2(svc, sid)
                if "Sheet1" in title_to_id and len(title_to_id) > 1:
                    svc.spreadsheets().batchUpdate(
                        spreadsheetId=sid,
                        body={"requests": [{"deleteSheet": {"sheetId": title_to_id["Sheet1"]}}]}
                    ).execute()
            except Exception:
                pass

        return jsonify({
            "ok": True,
            "status": "success",
            "spreadsheet_url": surl,
            "spreadsheet_id": sid,
            "tabs": tabs_created,
            "students_count": len(students),
            "message": f"Bootstrap done: {len(tabs_created)} tabs, {len(students)} students"
        }), 200

    except Exception as e:
        return jsonify({"ok": False, "status": "error", "message": str(e)}), 500


# --- SYNC ROSTER: Đồng bộ roster vào tab đã có ---

@gsheets_bp.route("/gsheets/sync_roster", methods=["POST"])
@_require_api_key
def gsheets_sync_roster():
    """
    Đồng bộ danh sách sinh viên từ Firestore roster vào một tab cụ thể.
    """
    try:
        teacher_id = get_teacher_id_from_req()
        if not teacher_id:
            return jsonify({"ok": False, "message": "Missing X-Teacher-ID"}), 400
        term_id = get_term_id_from_req()
        class_id = get_class_id_from_req()

        body = request.json or {}
        tab_title = _sanitize_sheet_title(body.get("sheet_title") or body.get("subjectCode") or "")
        if not tab_title:
            return jsonify({"ok": False, "message": "Missing sheet_title or subjectCode"}), 400

        sid, surl, _, err = _ensure_spreadsheet_v2(teacher_id, term_id, class_id)
        if err:
            return jsonify({"ok": False, "status": "need_google_auth", "message": err}), 200

        creds, cerr = _get_google_creds(teacher_id)
        if cerr:
            return jsonify({"ok": False, "message": cerr}), 200
        svc = _sheets_service(creds)

        tab_title = _ensure_subject_tab_header_v2(svc, sid, tab_title)

        # Lấy roster từ Firestore
        roster_col = get_roster_collection(teacher_id, term_id, class_id)
        students = []
        if db:
            for d in db.collection(roster_col).stream():
                x = d.to_dict() or {}
                mssv = _safe(x.get("mssv", d.id))
                name = _safe(x.get("name", ""))
                if mssv:
                    students.append({"mssv": mssv, "name": name})
        students.sort(key=lambda s: s["mssv"])

        row_map, last_row = _sheet_row_map_v2(svc, sid, tab_title)
        next_row = last_row + 1
        write_updates = []
        added = 0
        for st in students:
            mssv = st["mssv"]
            if mssv not in row_map:
                write_updates.append({
                    "range": f"'{tab_title}'!A{next_row}:B{next_row}",
                    "values": [[mssv, st["name"]]]
                })
                row_map[mssv] = next_row
                next_row += 1
                added += 1

        if write_updates:
            svc.spreadsheets().values().batchUpdate(
                spreadsheetId=sid,
                body={"valueInputOption": "RAW", "data": write_updates}
            ).execute()

        return jsonify({
            "ok": True,
            "status": "success",
            "spreadsheet_url": surl,
            "tab": tab_title,
            "total_students": len(students),
            "new_rows_added": added
        }), 200

    except Exception as e:
        return jsonify({"ok": False, "status": "error", "message": str(e)}), 500


# --- BACKFILL STORAGE_PATH ---

@gsheets_bp.route('/backfill_storage_path', methods=['POST'])
def backfill_storage_path():
    """
    Quét tất cả FaceVectors và cập nhật storage_path cho các document thiếu.
    """
    try:
        if not db or not bucket:
            return jsonify({"status": "error", "message": "Firebase not initialized"}), 500
        
        def find_first_photo(prefix):
            for blob in bucket.list_blobs(prefix=prefix, max_results=1):
                return blob.name
            return None
        
        updated_count = 0
        skipped_count = 0
        not_found_count = 0
        
        teachers = db.collection("Teachers").stream()
        
        for teacher_doc in teachers:
            teacher_id = teacher_doc.id
            terms = db.collection("Teachers").document(teacher_id).collection("terms").stream()
            
            for term_doc in terms:
                term_id = term_doc.id
                classes = db.collection("Teachers").document(teacher_id).collection("terms").document(term_id).collection("classes").stream()
                
                for class_doc in classes:
                    class_id = class_doc.id
                    face_col = f"Teachers/{teacher_id}/terms/{term_id}/classes/{class_id}/FaceVectors"
                    face_docs = db.collection(face_col).stream()
                    
                    for face_doc in face_docs:
                        doc_id = face_doc.id
                        data = face_doc.to_dict() or {}
                        
                        existing_path = (data.get("storage_path") or "").strip()
                        if existing_path:
                            skipped_count += 1
                            continue
                        
                        name = (data.get("name") or "").strip()
                        mssv = (data.get("mssv") or doc_id).strip()
                        
                        if not name or not mssv:
                            skipped_count += 1
                            continue
                        
                        prefix = f"Teachers/{teacher_id}/terms/{term_id}/classes/{class_id}/photos/{name}_{mssv}/"
                        photo_path = find_first_photo(prefix)
                        
                        if not photo_path and " " in prefix:
                            prefix_alt = prefix.replace(" ", "_")
                            photo_path = find_first_photo(prefix_alt)
                        
                        if photo_path:
                            db.collection(face_col).document(doc_id).update({
                                "storage_path": photo_path
                            })
                            updated_count += 1
                        else:
                            not_found_count += 1
        
        return jsonify({
            "status": "success",
            "updated": updated_count,
            "skipped": skipped_count,
            "not_found": not_found_count,
            "message": f"Backfill completed: {updated_count} updated, {skipped_count} skipped, {not_found_count} not found"
        }), 200
        
    except Exception as e:
        traceback.print_exc()
        return jsonify({"status": "error", "message": str(e)}), 500
