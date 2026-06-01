FROM python:3.10-slim

# Install system dependencies for OpenCV and DeepFace
RUN apt-get update && apt-get install -y \
    libgl1 \
    libglib2.0-0 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Upgrade pip
RUN pip install --upgrade pip

# Install Python dependencies

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Copy source code
COPY . .

# Pre-download models removed (Offloaded to Modal)
# RUN python download_models.py

# Cloud Run injects PORT (default 8080)
ENV PORT 8080

# Keep timeout high for AI processing
CMD exec gunicorn --bind :$PORT --workers 1 --threads 8 --timeout 0 main:app