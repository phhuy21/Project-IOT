import subprocess
import json

cmd = [
    "gcloud.cmd", "logging", "read",
    "resource.type=\"cloud_run_revision\" AND resource.labels.service_name=\"face-api-singapore\"",
    "--project=facecheckin-ai",
    "--limit=20",
    "--format=json"
]

try:
    out = subprocess.check_output(cmd)
    logs = json.loads(out)
    for l in logs:
        print("-------------")
        if 'textPayload' in l:
            print(l['textPayload'])
        elif 'protoPayload' in l:
            print(l['protoPayload'])
        else:
            print(json.dumps(l, indent=2))
except Exception as e:
    print("Failed to run gcloud:")
    print(e)
