# Project IOT - FaceCheckin

FaceCheckin is an IoT attendance project that combines a Unity client with a Python AI backend. The client captures classroom attendance input, while the backend handles face recognition, roster data, attendance logs, Firebase storage, and Google Sheets sync.

## Repository Layout

- `FaceCheckin_AI/FaceCheckin_AI/` - Flask backend, Firebase integration, FAISS matching, Modal GPU workers, and API routes.
- `gui/gui/gui/gui/` - Unity client project.
- `docs/` - architecture notes, diagrams, and project documentation.
- `firebase.json` and `storage.rules` - Firebase project configuration.

## Backend

Main entry point:

```bash
cd FaceCheckin_AI/FaceCheckin_AI
python main.py
```

Install dependencies:

```bash
pip install -r requirements.txt
```

The backend expects Firebase credentials from Application Default Credentials or a local `serviceAccountKey.json`. That file is intentionally ignored and must not be committed.

## Unity Client

Open the Unity project from:

```text
gui/gui/gui/gui
```

Unity generated folders such as `Library`, `Temp`, `Obj`, `Build`, `Logs`, and local IDE files are ignored.

## Large Files

This repository uses Git LFS for large Firebase native plugin files required by the Unity client. After cloning, install Git LFS and run:

```bash
git lfs pull
```
