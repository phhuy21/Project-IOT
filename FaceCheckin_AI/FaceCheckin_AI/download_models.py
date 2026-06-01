from deepface import DeepFace
import os

os.environ["TF_ENABLE_ONEDNN_OPTS"] = "0"
print("⏳ Pre-downloading AI Models...")

# Download Face Recognition Model
# Must match MODEL_NAME in main.py
DeepFace.build_model("Facenet512") 

print("✅ Models downloaded!")
