using System;
using System.Threading.Tasks; // [FIX] For Task.Run
using System.Collections; // <-- FIX LỖI IEnumerator YÊU CẦU ĐỐI SỐ
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Firebase;
using Firebase.Database;
using Firebase.Extensions;
using Firebase.Storage;      // <--- Sửa lỗi FirebaseStorage
using Mediapipe.Unity;
using Mediapipe.Unity.Sample;
using Mediapipe.Unity.Sample.FaceDetection;
using UnityEngine;
using UnityEngine.Networking; // <-- FIX LỖI UnityWebRequest, DownloadHandler, UploadHandler
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using ExcelDataReader;
using System.Data;


[System.Serializable]
public class LatencyMs
{
    public float embedding;
    public float matching;
    public float total_backend;
}

[System.Serializable]
public class RecognitionResponse
{
    public string status;
    public string name;
    public string mssv;
    public float distance;     // server trả field này
    public float match_percent; // [NEW] server trả thêm field này
    public float confidence;   // giữ lại để tương thích ngược nếu cần
    public string message;
    
    // [NEW] Latency Breakdown
    public LatencyMs latency_ms;

    // NEW: returned from server on success
    public string log_id;
    public string log_date;
    public string log_time;
    public long log_vn_epoch_ms;
}

[System.Serializable]
public class AttendanceLogItem
{
    public string id;
    public string name;
    public string mssv;
    public string date;
    public string time;
    public string status;
    public float distance;
    public float match_percent;
    public long vn_epoch_ms;
}

[System.Serializable]
public class LogsResponse
{
    public int count;
    public AttendanceLogItem[] logs;
}

[System.Serializable]
public class AttendanceLog
{
    public string id;
    public string studentName;
    public string mssv;
    public string time;    // Giờ (08:30:45)
    public string date;    // [NEW] Ngày (01/01/2026)
    public string status;  // "OnTime" hoặc "Late"
    public float confidence;
    public long timestamp; // Dùng để sort nếu cần
    public bool isSynced = true; // [NEW] Track if log is synced to server
}

// Class dùng để tính toán thống kê
public class StudentStat
{
    public string Name;
    public string MSSV;
    public int TotalCheckIn;
    public int LateCount;
    public int OnTimeCount;
    public int AbsentCount; // [NEW] Số buổi vắng
    public HashSet<string> AttendedDates = new HashSet<string>(); // [NEW] Lưu các ngày đã điểm danh
    public bool IsRegistered; // [NEW] Đã đăng ký khuôn mặt chưa
}

// [NEW] Class lưu thông tin buổi học trong thời khóa biểu
[System.Serializable]
public class ScheduleSession
{
    public string id;               // [NEW] Unique ID
    public string subjectName;      // Môn học  
    public string subjectCode;      // Mã môn (COOL325364)
    public string classCode;        // Mã lớp tín chỉ
    public string level;            // Hệ (Đại học, Cao đẳng)
    public int dayOfWeek;           // 2-7 (Thứ 2 - Thứ 7)
    public string periods;          // "1->5", "7->11" (Legacy string)
    public int startPeriod;         // [NEW] Tiết bắt đầu (int)
    public int endPeriod;           // [NEW] Tiết kết thúc (int)
    public string room;             // Phòng học
    public string colorHex;         // Màu hiển thị (hex)
}

// ================= GOOGLE SHEETS - RESPONSE MODELS =================

[System.Serializable]
public class GSheetEnsureRes
{
    // /gsheets/ensure
    // Expected: status = "success" | "need_google_auth" | "error"
    public string status;
    public string url;
    public string message;
    public string error;
    public bool ok;
    public string spreadsheet_url;
    public string spreadsheet_id;
    public bool created;
    public bool need_google_auth;
}

[System.Serializable]
public class GSheetBootstrapResponse
{
    // /gsheets/bootstrap
    public string status;                 // "success" | "need_google_auth" | "error"
    public string spreadsheet_id;
    public string spreadsheet_url;
    public int tabs_created_or_ensured;
    public bool has_phase2;
}

[System.Serializable]
public class GSheetRosterSyncResponse
{
    // /gsheets/sync_roster
    public bool ok;
    public string spreadsheet_url;
    public int tabs_processed;
    public int rows_synced;
}

[System.Serializable]
public class GSheetWeekUpdateResult
{
    public int rows_written;
    public int cells_written;
    public int new_roster_rows;
}

[System.Serializable]
public class GSheetWeekUpdateResponse
{
    // /gsheets/update_week
    public bool ok;
    public string status;         // optional: "success" | "error"
    public bool already_synced;   // nếu backend idempotent theo sessionKey
    public string spreadsheet_url;
    public int phase;             // 1 hoặc 2
    public int week;              // 1..9
    public string col;            // ví dụ "C", "D", ...
    public GSheetWeekUpdateResult result;
    public string message;
    public string error;
}

[System.Serializable]
public class FinalizeResp
{
    // /gsheets/finalize
    public bool ok;
    public string status;     // "success" | "error" | ...
    public int updated;
    public int phase;
    public int week;
    public string col;
    public GSheetWeekUpdateResult result;
    public string spreadsheet_url;
    public string message;
    public string error;
}

[System.Serializable]
public class OpenSheetResp
{
    // (endpoint mở sheet trực tiếp)
    public string status;
    public string spreadsheet_id;
    public string spreadsheet_url;
    public string message;
    public string error;
}


public class DashboardController : MonoBehaviour
{
    // --- CẤU HÌNH ENGINE AI (FILE .EXE) ---
    [Tooltip("Địa chỉ API Cloud Run đã deploy")]
    public string cloudRunApiUrl = "https://facecheckin-ai-650095007289.asia-southeast1.run.app";
    // public string cloudRunApiUrl = "http://localhost:5000"; // [DEBUG] Switch to Localhost

    private Button _btnAddFace;

    [Header("Giao diện")]
    public Texture2D schoolLogo;
    private string storageBucketLink = "gs://facecheckin-ai.firebasestorage.app";

    private bool _isCameraRunning = false;
    private Texture2D _capturedFront;
    private Texture2D _capturedLeft;
    private Texture2D _capturedRight;

    private Coroutine _recognitionCoroutine;
    [SerializeField] private FaceDetectorRunner faceDetector;
    private Rect _lastFaceRectNorm;
    private bool _hasFace = false;

    // [NEW] Client-Side Tracking Logic
    [System.Serializable]
    public struct FaceTracker
    {
        public string studentId;
        public string studentName;
        public float matchPercent;
        public float lastSeenTime;
        public Rect lastRect;
    }
    
    private FaceTracker _activeTracker;
    private const float TRACKING_TIMEOUT = 2.0f; // Track within 2 seconds
    private const float TRACKING_IOU_THRESHOLD = 0.5f; // Intersection over Union threshold



    private enum CameraMode { Off, Recognize, Register }
    private CameraMode _cameraMode = CameraMode.Off;


    private Button _btnCamera;
    private VisualElement _sidebarCameraPreview;
    private VisualElement _cameraMainContainer;

    // P1 FIXME: Face Tracking UI
    private VisualElement _faceTrackingBox;
    private Label _faceInfoLabel;
    private VisualElement _mirrorContainer; // New Wrapper for Fliping
    private RecognitionResponse _lastVerifyResult;

    private Label _lblClock;
    private UIDocument _doc;
    private VisualElement _root;

    // --- BIẾN UI TOOLKIT MỚI (từ UXML) ---
    private UIDocument _uiDoc;
    private VisualElement _uiRoot;
    private Button _uiBtnCamera;
    private VisualElement _uiCameraPreview;
    private Label _uiLblStatus;
    private Label _uiLblLastStudent;
    private Label _uiLblClock;
    private Label _uiLblDateWeek;
    private string _cachedDateWeekText = "";
    private int _lastHeaderSecond = -1;
    private DropdownField _drpChartWeekSelector; // [NEW] Chart Week Selector Dropdown
    private DropdownField _drpDaySelector; // [NEW] Day Selector for Home Stats

    // ListView và Template cho danh sách log
    private ListView _mainListView;
    private ListView _listTopAbsent; // [NEW] Top Absent ListView
    
    // [NEW] Weekly trend chart (10 weeks)
    private VisualElement _pieChartContainer;
    private Label _lblPiePresent, _lblPieLate, _lblPieAbsent;
    private VisualElement _weeklyTrendCanvas;
    private Label _lblOverviewHint;
    private Label _lblWeeklyTrendValue;
    
    // ================= GOOGLE SHEETS AUTO SYNC =================
    private DateTime _nextGSheetAutoCheckAt = DateTime.MinValue;
    private HashSet<string> _gsheetSyncedSessionKeys = new HashSet<string>();
    private const int MIN_LOGS_AUTO_SYNC_MIN = 2;  // lớp nhỏ vẫn sync được
    private const int MIN_LOGS_AUTO_SYNC_MAX = 5;  // lớp lớn cần nhiều log hơn
    
    // [NEW] Current Session Info (for Finalize)
    private string _currentSubjectCode = "";  // CNTT1, CTRR...
    private string _currentSubjectName = "";  // Tên môn học
    
    [SerializeField] private VisualTreeAsset logItemTemplate; // Kéo thả file UXML template vào đây trong Inspector
    [SerializeField] private VisualTreeAsset studentItemTemplate; // Template cho StudentListView
    [SerializeField] private VisualTreeAsset modalStudentDetailTemplate; // Modal chi tiết sinh viên
    [SerializeField] private VisualTreeAsset modalAttendanceResultTemplate; // [NEW] Modal kết quả điểm danh

    // [NEW] DASHBOARD UI VARIABLES
    private Label _lblStatTotal, _lblStatPresent, _lblStatAbsent, _lblStatLate, _lblStatPercent;
    private Button _btnQuickAttendance, _btnQuickAddStudent;

    // --------------------------------------
    private VisualElement _listContainer; // Container chứa nội dung chính (Log hoặc Stat)
    private VisualElement _statsHeaderRow; // Hàng tiêu đề cho bảng thống kê (ẩn/hiện)

    // Student List Fields
    private ListView _studentListView;
    private List<StudentStat> _studentDataList = new List<StudentStat>();
    private VisualElement _modalContainer;

    // UI References
    private Label _lblTotal, _lblOnTime, _lblLate, _lblListTitle;

    // Dữ liệu
    private List<AttendanceLog> _allLogs = new List<AttendanceLog>();
    private Queue<AttendanceLog> _incomingQueue = new Queue<AttendanceLog>();
    private List<string> _deletedIds = new List<string>(); // Danh sách ID đã xóa
    private object _lock = new object();

    private string _currentViewMode = "All"; // "All", "OnTime", "Late", "Stats"

    // [NEW] Verify Configuration & State
    private const float VERIFY_DISTANCE_THRESHOLD = 300f; // Sync with Server
    private const long VERIFY_DEBOUNCE_TICKS = 10_000_000L * 10; // 10 Seconds Debounce
    private long _lastRecognizedTick = 0;
    private string _lastRecognizedMssv = "";
    private DatabaseReference _dbRef;
    private bool _logsInFlight = false;
    private bool _skipNextProfilesFetchInRegisterTab = false; // [OPT] Skip fetch if just refreshed
    private int _tenantContextVersion = 0; // ✅ chống lẫn dữ liệu khi đổi lớp trong lúc đang fetch/sync
    private bool _syncingWeekSelectors = false; // ✅ Chống loop callback khi sync 2 dropdown tuần
    private const int LOG_FETCH_LIMIT = 200;

    // --- [TEST] Google Sheets JSON Parse Test ---
    [ContextMenu("TEST GSheet Parse")]
    private void TestGSheetParse()
    {
        string json = "{\"ok\":true,\"already_synced\":false,\"spreadsheet_url\":\"https://docs.google.com/spreadsheets/d/abc\",\"phase\":1,\"week\":3,\"col\":\"E\",\"result\":{\"rows_written\":40,\"cells_written\":80,\"new_roster_rows\":0}}";
        var res = JsonUtility.FromJson<GSheetWeekUpdateResponse>(json);

        Debug.Log($"[GSheet TEST] ok={res.ok} url={res.spreadsheet_url} phase={res.phase} week={res.week} col={res.col}");
        Debug.Log($"[GSheet TEST] result rows={res.result.rows_written} cells={res.result.cells_written} new={res.result.new_roster_rows}");
    }

    [ContextMenu("Print API Base URL")]
    private void PrintApiBaseUrl()
    {
        Debug.Log($"[API] BaseUrl={cloudRunApiUrl}");
        Debug.Log($"[API] TeacherID={_currentTeacherId}");
    }

    // --- [NEW] RECOGNITION CONFIGURATION (A) ---
    [Header("Recognition Logic & Crop")]
    [SerializeField] private bool sendCroppedFace = true;
    [SerializeField] private float cropMargin = 0.25f; // 25% margin
    [SerializeField] private int cropMinSizePx = 160;
    [SerializeField] private int jpgQuality = 80;
    [SerializeField] private bool debugSaveJpg = false;       // (D) Tuỳ chọn lưu ảnh debug
    [SerializeField] private bool mirrorXWhenCropping = true; // Fix camera selfie bị ngược

    // --- [NEW] COOLDOWN & CONFIRM CONFIG (B, C) ---
    [Header("Anti-Spam & Confirmation")]
    [SerializeField] private float verifyInterval = 0.15f; // Loop rate
    [SerializeField] private float failCooldown = 2.0f;    // Chờ 2s nếu fail/lỗi mới đc thử lại
    [SerializeField] private int requiredConsecutiveHits = 1; // Cần N lần trùng MSSV mới chốt (Giảm từ 2 xuống 1)
    [SerializeField] private float minMatchPercentToCount = 0f; // Điểm tối thiểu để tính là 1 hit

    // --- Runtime Vars ---
    private float _nextVerifyAllowedTime = 0f;
    private string _candidateMssv = null;
    private int _candidateCount = 0;
    private float _candidateLastSeenTime = 0f;

    // --- BẢNG MÀU HIỆN ĐẠI (MODERN PALETTE - REFRESHED) ---
    // Nền: Xám xanh rất nhạt
    private readonly Color colBg = new Color(0.96f, 0.97f, 0.99f);

    // Sidebar: Sử dụng Gradient (fake bằng màu solid nhưng chọn tone sang hơn)
    private readonly Color colSidebar = new Color(0.12f, 0.14f, 0.2f);

    // Accent: Xanh dương tươi
    private readonly Color colAccent = new Color(0.25f, 0.55f, 0.95f);

    // Text Colors
    private readonly Color colTextDark = new Color(0.2f, 0.2f, 0.3f);
    private readonly Color colTextLight = new Color(0.6f, 0.6f, 0.7f);

    // --- MÀU TRẠNG THÁI (SOFT & VIBRANT) ---
    private readonly Color colGreen = new Color(0.0f, 0.8f, 0.5f); // Vibrant Mint
    private readonly Color colRed = new Color(0.95f, 0.4f, 0.4f);   // Soft Red
    private readonly Color colOrange = new Color(1f, 0.6f, 0.2f); // Orange for Absent
    private readonly Color colBlue = new Color(0.04f, 0.52f, 0.89f); // #0984E3 - Blue

    // Thêm biến để lưu từ khóa tìm kiếm
    private string _searchText = "";
    private TextField _searchInput;

    // --- CẤU HÌNH (MỚI) ---
    private string _lateThreshold = "07:00"; // Mặc định 7 giờ sáng
    private string _dateFilterMode = "All"; // "All", "Today", "Yesterday"

    // --- NEW: RECOGNITION OPTIMIZATION VARS ---
    private bool _verifyInFlight = false;
    private int _consecutiveHit = 0;
    private const float VERIFY_INTERVAL = 0.1f;   // [MOD] 0.1f (10 FPS) as requested
    private const float REQUIRED_PERCENT = 88f;    // Match percent threshold from server

    private RenderTexture _capRT;
    private Texture2D _capTex;
    private Label _camLabel; // [NEW] Promoted from local var to field
    private Texture2D _uiPreviewLastTex; // [FIX] Track UI Preview Texture to prevent memory leak
    private IVisualElementScheduledItem _uiPreviewJob; // [FIX] Track scheduled job to pause on stop
    private IVisualElementScheduledItem _cameraMainJob; // [FIX] Track main camera scheduler

    // [NEW] Sidebar Collapse
    private VisualElement _uiSidebar;
    private Button _btnSidebarToggle;
    private bool _isSidebarCollapsed;

    // UI Notification Container
    private VisualElement _toastContainer;
    
    // [NEW] Data Latency Card UI
    private Label _lblLatencyTotal, _lblLatencyDetection, _lblLatencyAlignment, _lblLatencyEmbedding, _lblLatencyMatching, _lblLatencyNetwork, _lblLatencyNote;
    private float _lastAlignMs = 0f; // cập nhật mỗi lần crop
    private float _latDetMs, _latAlignMs, _latEmbMs, _latMatchMs, _latNetMs, _latTotalMs;

    // ====== LOG HIDE LOCAL (không xóa Firebase) ======
    private HashSet<string> _hiddenLogIds = new HashSet<string>();

    [Serializable]
    private class HiddenLogIdsWrapper
    {
        public string[] ids;
    }



    // [NEW] Login UI Elements
    private VisualElement _loginOverlay;
    private TextField _inpLoginUser;
    private TextField _inpLoginPass;
    private TextField _inpLoginEmail; // [NEW] Email field for Register
    private Button _btnLoginInfo;
    private Label _lblRegister;
    private Label _lblTitle;
    private bool _isLoggedIn = false;
    private bool _isRegisterMode = false;
    private string _currentTeacherId = "";

    // [NEW] Student List UI & Search
    // _studentListView already defined above
    private TextField _inpSearchStudent;
    private List<FaceProfile> _allProfilesCache = new List<FaceProfile>(); // Cache for filtering

    // ====== SCHEDULE DATA (NEW) ======
    private List<ScheduleSession> _scheduleList = new List<ScheduleSession>();
    private string ScheduleDataPath =>
        System.IO.Path.Combine(Application.persistentDataPath,
            string.IsNullOrEmpty(_currentTeacherId) ? "schedule_data.json" : $"schedule_data_{_currentTeacherId}.json");

    // ====== TERM / PHASE CONFIG ======
    [Serializable]
    public class ScheduleDataWrapper
    {
        public List<ScheduleSession> sessions = new List<ScheduleSession>();
        public string schoolYear = "2025-2026";
        public int semester = 1;
        public string phase1Start = "";
        public string phase1End = "";
        public string phase2Start = "";
        public string phase2End = "";

        // Class switcher state
        public string selectedClassCode = "";
        public string selectedTermId = "legacy";
    }

    // ====== SYNC CLASSES (Schedule Bundle + Roster) ======
    [Serializable]
    private class ScheduleBundleResp
    {
        public string status;
        public string message;

        public List<ScheduleSession> sessions = new List<ScheduleSession>();
        public string schoolYear;
        public int semester;

        public string phase1Start, phase1End, phase2Start, phase2End;
        public string selectedClassCode;
        public string selectedTermId;
    }

    [Serializable]
    private class RosterStudentDto
    {
        public string name;
        public string mssv;
        public bool registered;
    }

    [Serializable]
    private class RosterGetResp
    {
        public string status;
        public string message;
        public RosterStudentDto[] students;
    }

    [Serializable]
    private class RosterImportReq
    {
        public RosterStudentDto[] students;
    }

    // Term UI fields
    private DropdownField _drpSchoolYear;
    private DropdownField _drpSemester;
    private TextField _inpPhase1Start, _inpPhase1End, _inpPhase2Start, _inpPhase2End;

    // Term config state
    private string _schoolYear = "2025-2026";
    private int _semester = 1;
    private string _playerPhase1Start = "";
    private string _playerPhase1End = "";
    private string _playerPhase2Start = "";
    private string _playerPhase2End = "";

    // Week map cache (label -> date range)
    private readonly Dictionary<string, (DateTime start, DateTime end)> _weekMap
        = new Dictionary<string, (DateTime start, DateTime end)>();

    // ====== TENANT CONTEXT (MULTI-CLASS) ======
    private string _currentTermId = "legacy";
    private string _currentClassCode = ""; // e.g. "IT001", "SE102"

    private static string SafeFileToken(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "none";
        foreach (char c in Path.GetInvalidFileNameChars()) raw = raw.Replace(c, '_');
        raw = raw.Replace(' ', '_');
        return raw;
    }

    private string SafeTeacherToken => SafeFileToken(string.IsNullOrEmpty(_currentTeacherId) ? "anon" : _currentTeacherId);
    private string TenantFileKey => $"{SafeFileToken(_currentTermId)}__{SafeFileToken(_currentClassCode)}";

    // Local files per class/term
    private string LogsLocalPath => Path.Combine(Application.persistentDataPath, $"attendance_logs_{SafeTeacherToken}_{TenantFileKey}.json");
    private string HiddenLogLocalPath => Path.Combine(Application.persistentDataPath, $"hidden_log_ids_{SafeTeacherToken}_{TenantFileKey}.json");

    // Computed term ID from _schoolYear + _semester (format: "2025-2026_HK2")
    private string CurrentTermId => string.IsNullOrEmpty(_schoolYear)
        ? "legacy"
        : $"{_schoolYear}_HK{_semester}";

    /// <summary>
    /// Apply multi-tenancy headers (Teacher, Term, Class) to a UnityWebRequest.
    /// </summary>
    private void ApplyTenantHeaders(UnityWebRequest req)
    {
        if (req == null) return;

        if (!string.IsNullOrEmpty(_currentTeacherId))
            req.SetRequestHeader("X-Teacher-ID", _currentTeacherId);

        req.SetRequestHeader("X-Term-ID", string.IsNullOrEmpty(_currentTermId) ? "legacy" : _currentTermId);

        if (!string.IsNullOrEmpty(_currentClassCode))
            req.SetRequestHeader("X-Class-ID", _currentClassCode);
    }

    // Schedule UI References
    private VisualElement _scheduleModalContainer;
    private Button _btnImportSchedule, _btnAddSession, _btnClearSchedule;
    private VisualElement[] _scheduleCols = new VisualElement[6]; // Thứ 2-7
    private VisualElement _todayScheduleContainer; // Home page
    private VisualElement _registerTabContainer; // [NEW] Fullscreen Register Tab

    // ====== REGISTER (FaceID-like) ======
    private string _regName;
    private string _regMssv;
    private bool _lastRegisterOk = true;
    private string _lastRegisterErr = "";

    private enum RegStage { Center, Left, Right, Done }
    private RegStage _regStage = RegStage.Center;
    private Coroutine _trainingCoroutine; // [NEW] Track coroutine to stop
    private float _stableTimer = 0f;
    private float _cooldownTimer = 0f;

    [SerializeField] private float holdSeconds = 0.5f;
    [SerializeField] private float shotCooldown = 0.8f;

    // Ngưỡng (độ)
    [SerializeField] private float centerYaw = 10f;
    [SerializeField] private float leftYaw = -20f;
    [SerializeField] private float rightYaw = 20f;
    [SerializeField] private float maxPitch = 12f;
    [SerializeField] private float maxRoll = 12f;

    // Mỗi stage chụp bao nhiêu ảnh
    [SerializeField] private int shotsPerStage = 4;
    private int _shotsTakenThisStage = 0;

    // Nếu camera trước bị mirror, đảo yaw
    [SerializeField] private bool invertYaw = false;

    // --- [THÊM MỚI] Biến phục vụ đăng ký 3 góc ---
    private List<Texture2D> _tempRegisterImages = new List<Texture2D>();
    private int _registerStep = 0; // 0: Chưa chụp, 1: Thẳng, 2: Trái, 3: Phải

    // --- [THÊM MỚI] Helper lấy ảnh Webcam ---
    private Texture2D CaptureCurrentFrame()
    {
        if (_capTex != null)
        {
             // Clone the current frame
             Texture2D snap = new Texture2D(_capTex.width, _capTex.height, TextureFormat.RGB24, false);
             snap.SetPixels(_capTex.GetPixels());
             snap.Apply();
             return snap;
        }
        return null;
    }

    // [NEW] Helper to get full frame from MediaPipe source
    private Texture2D CaptureFullFrame()
    {
        var imgSource = ImageSourceProvider.ImageSource;
        if (imgSource == null) return null;
        var src = imgSource.GetCurrentTexture();
        if (src == null) return null;

        return ConvertToTexture2D(src);
    }

    // [NEW] Robust Texture -> Texture2D converter
    private Texture2D ConvertToTexture2D(Texture source)
    {
        RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(source, rt);
        
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        
        Texture2D result = new Texture2D(source.width, source.height, TextureFormat.RGB24, false); // No MipMap
        result.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
        result.Apply();
        
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        return result;
    }

    private void UpdateFaceBBox()
    {
        // UpdateFaceBBox: Dùng kết quả thật từ MediaPipe
        if (TryGetFaceRectNormFromLastResult(out Rect b))
        {
            _hasFace = true;
            _lastFaceRectNorm = b;

            // [FIX] Update UI Box Position
            if (_faceTrackingBox != null && _cameraMainContainer != null)
            {
                _faceTrackingBox.style.display = DisplayStyle.Flex;

                // Map Norm (0..1) to UI Size (640x480)
                float w = _cameraMainContainer.resolvedStyle.width;
                float h = _cameraMainContainer.resolvedStyle.height;
                if (float.IsNaN(w) || w == 0) w = 640;
                if (float.IsNaN(h) || h == 480) h = 480;

                // MediaPipe Y is Top-Down? Usually needs check. 
                // Unity UI Toolkit top-left is (0,0).
                // MediaPipe Norm Rect (0,0) is usually top-left.

                _faceTrackingBox.style.left = b.x * w;
                _faceTrackingBox.style.top = b.y * h;
                _faceTrackingBox.style.width = b.width * w;
                _faceTrackingBox.style.height = b.height * h;
            }
        }
        else
        {
            _hasFace = false;
            // Hide box if lost face
            if (_faceTrackingBox != null) _faceTrackingBox.style.display = DisplayStyle.None;
        }
    }

    void Awake()
    {
        // FORCE OVERRIDE to the correct environment! 
        // Prevents the serialized "face-api-singapore" in scenes from breaking the URL.
        cloudRunApiUrl = "https://facecheckin-ai-650095007289.asia-southeast1.run.app";
    }

    void OnEnable()
    {
        // [FIX] Enable IME support to prevent ArgumentOutOfRangeException with Vietnamese input
        Input.imeCompositionMode = IMECompositionMode.On;

        // 1. Lấy tham chiếu document
        _uiDoc = GetComponent<UIDocument>();
        if (_uiDoc == null) { Debug.LogError("Thiếu UIDocument!"); return; }

        _lateThreshold = PlayerPrefs.GetString("LateThreshold", "07:00"); // [FIX] Load Settings

        // [FIX] Load schedule FIRST to restore _currentClassCode
        LoadScheduleFromLocal();
        // [FIX] Then load logs (now uses correct class-specific path)
        LoadLogsLocal();

        // --- CODE MỚI: LẤY UI TỪ FILE UXML ---
        // --- CODE MỚI: LẤY UI TỪ FILE UXML ---
        _uiRoot = _uiDoc.rootVisualElement;

        EnsureToastOverlay(); // [NEW] Ensure toast container globally

        SetupClassSwitcher();  // ✅ Enable class switching
        BindClassPickerModal(); // ✅ Bind class picker modal
        SetupLoginUI(); // [NEW] Init Login
        SetupStudentListLogic(); // [NEW] Init Student List & Search
        
        SetupStatsTab(); // [NEW] Init Stats UI & Latency Card

        // [FIX] Explicitly setup Google buttons as requested
        SetupConnectGoogleButton();
        SetupOpenGSheetButton();
        SetupOpenGoogleSheetStudentListButton(); // [NEW] Added as requested
        SetupSidebarCollapse(); // [NEW] Sidebar Collapse Toggle

        _root = _uiRoot; // [FIX] Gán _root để các hàm cũ (LoadBackground) chạy được

        // Tìm các thành phần (Đảm bảo tên khớp với file .uxml bạn đã tạo)
        _uiBtnCamera = _uiRoot.Q<Button>("BtnToggleCamera");
        _uiCameraPreview = _uiRoot.Q<VisualElement>("CameraPreview"); // Container đen chứa cam
        _uiLblStatus = _uiRoot.Q<Label>("LblStatus");
        _uiLblLastStudent = _uiRoot.Q<Label>("LblLastStudent");
        _uiLblClock = _uiRoot.Q<Label>("LblClock");

        // [NEW] Set sidebar logo
        var sidebarLogo = _uiRoot.Q<VisualElement>("SidebarLogo");
        if (sidebarLogo != null && schoolLogo != null)
        {
            sidebarLogo.style.backgroundImage = new StyleBackground(schoolLogo);
            sidebarLogo.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;
        }

        // [NEW] Bind và khởi tạo ngày/tuần hiện tại (theo đợt học kỳ)
        _uiLblDateWeek = _uiRoot.Q<Label>("LblDateWeek");
        UpdateHeaderDateWeek(force: true);

        // Gán sự kiện cho nút Camera mới
        if (_uiBtnCamera != null)
        {
            _uiBtnCamera.clicked += TogglePythonScript;
        }

        // --- BIND BTN HOME, HISTORY & CLASS LIST ---
        var btnHome = _uiRoot.Q<Button>("BtnHome");


        var btnClassList = _uiRoot.Q<Button>("BtnClassList");
        var btnStats = _uiRoot.Q<Button>("BtnStats"); // [NEW] Stats Tab

        // Cache UI elements for tab switching
        var cameraPanel = _uiRoot.Q<VisualElement>("CameraPreview");
        var mainContentContainer = _uiRoot.Q<VisualElement>("MainContentContainer");
        var historyContainer = _uiRoot.Q<VisualElement>("HistoryContainer"); // [NEW]
        var studentListContainer = _uiRoot.Q<VisualElement>("StudentListContainer");
        _modalContainer = _uiRoot.Q<VisualElement>("ModalContainer");

        // Logic chuyển Tab - HOME
        if (btnHome != null) btnHome.clicked += () =>
        {
            SwitchTabUI(btnHome, btnClassList, btnStats);
            _currentViewMode = "All";

            // SHOW Home Content
            if (mainContentContainer != null) mainContentContainer.style.display = DisplayStyle.Flex;
            if (historyContainer != null) historyContainer.style.display = DisplayStyle.None;
            if (studentListContainer != null) studentListContainer.style.display = DisplayStyle.None;
            if (_statsContainer != null) _statsContainer.style.display = DisplayStyle.None;

            // HOME: Ẩn Camera Panel (Popup)
            if (cameraPanel != null) cameraPanel.style.display = DisplayStyle.None;

            // HOME: Ẩn Attendance Container
            var attendanceContainer = _uiRoot.Q<VisualElement>("AttendanceContainer");
            if (attendanceContainer != null) attendanceContainer.style.display = DisplayStyle.None;

            // [FIX] Ẩn Schedule và Settings Container - Sửa lỗi chồng chéo
            if (_scheduleContainer != null) _scheduleContainer.style.display = DisplayStyle.None;
            if (_settingsContainer != null) _settingsContainer.style.display = DisplayStyle.None;
            if (_registerTabContainer != null) _registerTabContainer.style.display = DisplayStyle.None; // [FIX] Hide Register

            // [NEW] Animate
            AnimateEntry(mainContentContainer);

            // Only refresh stats if needed, not the list
            UpdateStatsFromLogs();
        };

        // Logic chuyển Tab - HISTORY


        // Logic chuyển Tab - CLASS LIST [NEW]
        if (btnClassList != null) btnClassList.clicked += () =>
        {
            SwitchTabUI(btnClassList, btnHome, btnStats);

            // HIDE Home Content
            if (mainContentContainer != null) mainContentContainer.style.display = DisplayStyle.None;
            if (historyContainer != null) historyContainer.style.display = DisplayStyle.None;
            // SHOW Student List
            // SHOW Student List
            // SHOW Student List
            if (studentListContainer != null)
            {
                studentListContainer.style.display = DisplayStyle.Flex;
                FetchAndRenderStudentList(); // [NEW] Load data
                AnimateEntry(studentListContainer);
            }
            if (_statsContainer != null) _statsContainer.style.display = DisplayStyle.None; // Hide Stats

            // HIDE Attendance Container
            var attendanceContainer = _uiRoot.Q<VisualElement>("AttendanceContainer");
            if (attendanceContainer != null) attendanceContainer.style.display = DisplayStyle.None;
            // HIDE Camera Panel
            if (cameraPanel != null) cameraPanel.style.display = DisplayStyle.None;
            // HIDE Schedule & Settings
            if (_scheduleContainer != null) _scheduleContainer.style.display = DisplayStyle.None;
            if (_settingsContainer != null) _settingsContainer.style.display = DisplayStyle.None;
            if (_registerTabContainer != null) _registerTabContainer.style.display = DisplayStyle.None; // [FIX] Hide Register

            FetchAndRenderStudentList();

            // [NEW] Animate
            AnimateEntry(studentListContainer);
        };

        // Logic chuyển Tab - STATISTICS [NEW]
        // Logic chuyển Tab - STATISTICS [NEW]
        if (btnStats != null) btnStats.clicked += () =>
        {
            SwitchTabUI(btnStats, btnHome, btnClassList);

            // HIDE others
            if (mainContentContainer != null) mainContentContainer.style.display = DisplayStyle.None;
            if (historyContainer != null) historyContainer.style.display = DisplayStyle.None;
            if (studentListContainer != null) studentListContainer.style.display = DisplayStyle.None;
            if (cameraPanel != null) cameraPanel.style.display = DisplayStyle.None;
            var attendanceContainer = _uiRoot.Q<VisualElement>("AttendanceContainer");
            if (attendanceContainer != null) attendanceContainer.style.display = DisplayStyle.None;

            if (_scheduleContainer != null) _scheduleContainer.style.display = DisplayStyle.None;
            if (_settingsContainer != null) _settingsContainer.style.display = DisplayStyle.None;
            if (_registerTabContainer != null) _registerTabContainer.style.display = DisplayStyle.None; // [FIX] Hide Register

            // SHOW Stats
            if (_statsContainer != null)
            {
                _statsContainer.style.display = DisplayStyle.Flex;
                AnimateEntry(_statsContainer);
                UpdateStatsTab(); // Refresh data
            }
        };

        // [NEW] Logic chuyển Tab - SCHEDULE
        var btnSchedule = _uiRoot.Q<Button>("BtnSchedule");
        if (btnSchedule != null) btnSchedule.clicked += () =>
        {
            // Reset UI styles (Manual for now)
            if (btnHome != null) btnHome.RemoveFromClassList("active");

            if (btnClassList != null) btnClassList.RemoveFromClassList("active");
            if (btnStats != null) btnStats.RemoveFromClassList("active");
            var btnSettings = _uiRoot.Q<Button>("BtnSettings");
            if (btnSettings != null) btnSettings.RemoveFromClassList("active");

            btnSchedule.AddToClassList("active");

            // Hide All
            if (mainContentContainer != null) mainContentContainer.style.display = DisplayStyle.None;
            if (historyContainer != null) historyContainer.style.display = DisplayStyle.None;
            if (studentListContainer != null) studentListContainer.style.display = DisplayStyle.None;
            if (_statsContainer != null) _statsContainer.style.display = DisplayStyle.None;
            if (cameraPanel != null) cameraPanel.style.display = DisplayStyle.None;
            var attendanceContainer = _uiRoot.Q<VisualElement>("AttendanceContainer");
            if (attendanceContainer != null) attendanceContainer.style.display = DisplayStyle.None;
            if (_settingsContainer != null) _settingsContainer.style.display = DisplayStyle.None;
            if (_registerTabContainer != null) _registerTabContainer.style.display = DisplayStyle.None; // [FIX] Hide Register

            // Show Schedule
            if (_scheduleContainer != null)
            {
                _scheduleContainer.style.display = DisplayStyle.Flex;
                AnimateEntry(_scheduleContainer);
            }
        };

        // [NEW] Logic CHANGE PASSWORD
        var btnChangePass = _uiRoot.Q<Button>("BtnChangePassword");
        if (btnChangePass != null) btnChangePass.clicked += OnClickChangePassword;

        var btnSettings = _uiRoot.Q<Button>("BtnSettings");
        if (btnSettings != null) btnSettings.clicked += () =>
        {
            // Reset UI styles
            if (btnHome != null) btnHome.RemoveFromClassList("active");

            if (btnClassList != null) btnClassList.RemoveFromClassList("active");
            if (btnStats != null) btnStats.RemoveFromClassList("active");
            if (btnSchedule != null) btnSchedule.RemoveFromClassList("active");

            btnSettings.AddToClassList("active");

            // Hide All
            if (mainContentContainer != null) mainContentContainer.style.display = DisplayStyle.None;
            if (historyContainer != null) historyContainer.style.display = DisplayStyle.None;
            if (studentListContainer != null) studentListContainer.style.display = DisplayStyle.None;
            if (_statsContainer != null) _statsContainer.style.display = DisplayStyle.None;
            if (cameraPanel != null) cameraPanel.style.display = DisplayStyle.None;
            var attendanceContainer = _uiRoot.Q<VisualElement>("AttendanceContainer");
            if (attendanceContainer != null) attendanceContainer.style.display = DisplayStyle.None;
            if (_scheduleContainer != null) _scheduleContainer.style.display = DisplayStyle.None;
            if (_registerTabContainer != null) _registerTabContainer.style.display = DisplayStyle.None; // [FIX] Hide Register

            // Show Settings
            if (_settingsContainer != null)
            {
                _settingsContainer.style.display = DisplayStyle.Flex;
                AnimateEntry(_settingsContainer);
            }
        };

        // [NEW] Bind Settings Save Button
        var inpTimeStart = _uiRoot.Q<TextField>("InpTimeStart");
        var btnSaveSettings = _uiRoot.Q<Button>("BtnSaveSettings");
        var lblStatLateSub = _uiRoot.Q<Label>("LblStatLateSub");

        // Initialize input with saved value
        if (inpTimeStart != null) inpTimeStart.value = _lateThreshold;

        // [FIX] Initialize Home Dashboard label with saved value
        if (lblStatLateSub != null) lblStatLateSub.text = $"Sau {_lateThreshold}";

        if (btnSaveSettings != null) btnSaveSettings.clicked += () =>
        {
            if (inpTimeStart != null)
            {
                _lateThreshold = inpTimeStart.value?.Trim() ?? "07:00";
                PlayerPrefs.SetString("LateThreshold", _lateThreshold);
                PlayerPrefs.Save();

                // [FIX] Update Home Dashboard label
                if (lblStatLateSub != null) lblStatLateSub.text = $"Sau {_lateThreshold}";

                ShowToast($"✅ Đã lưu: Giờ vào lớp = {_lateThreshold}", colGreen);
            }
        };

        // [NEW] Bind Change Password button inside Settings tab
        var btnChangePassSettings = _uiRoot.Q<Button>("BtnChangePasswordSettings");
        if (btnChangePassSettings != null) btnChangePassSettings.clicked += OnClickChangePassword;

        // [NEW] Bind Export CSV button in Settings tab
        var btnExportCSVSettings = _uiRoot.Q<Button>("BtnExportCSVSettings");
        if (btnExportCSVSettings != null) btnExportCSVSettings.clicked += () =>
        {
            ExportStatsToCSV();
        };

        // [NEW] Bind Clear Cache button in Settings tab
        var btnClearCacheSettings = _uiRoot.Q<Button>("BtnClearCacheSettings");
        if (btnClearCacheSettings != null) btnClearCacheSettings.clicked += () =>
        {
            // Clear local data
            _allLogs.Clear();
            _hiddenLogIds.Clear();
            PlayerPrefs.DeleteKey("HiddenLogIds");
            PlayerPrefs.Save();

            // Refresh UI
            RefreshMainContent();
            ShowToast("🗑️ Đã xóa dữ liệu cache!", colGreen);
        };

        var btnAttendance = _uiRoot.Q<Button>("BtnAttendance");
        var attendanceContainerRef = _uiRoot.Q<VisualElement>("AttendanceContainer");
        var btnToggleCameraAttendance = _uiRoot.Q<Button>("BtnToggleCameraAttendance");

        // [NEW] Bind Import & Manual Checkin (Gộp vào Tab)
        var btnImportStudent = _uiRoot.Q<Button>("BtnImportStudent");
        if (btnImportStudent != null) btnImportStudent.clicked += OnImportStudentClick;

        var btnManualCheckin = _uiRoot.Q<Button>("BtnManualCheckin");
        if (btnManualCheckin != null) btnManualCheckin.clicked += OnManualCheckinClick;

        var btnExportExcel = _uiRoot.Q<Button>("BtnExportAttendanceExcel");
        if (btnExportExcel != null) btnExportExcel.clicked += ExportWeeklyAttendanceToCSV;

        // [NEW] Helper Action for switching to Attendance Mode
        System.Action ShowAttendanceMode = () =>
        {
            SwitchTabUI(btnAttendance, btnHome, btnClassList, btnStats);

            // Hide others
            if (mainContentContainer != null) mainContentContainer.style.display = DisplayStyle.None;
            if (historyContainer != null) historyContainer.style.display = DisplayStyle.None;
            if (studentListContainer != null) studentListContainer.style.display = DisplayStyle.None;
            if (_statsContainer != null) _statsContainer.style.display = DisplayStyle.None;
            if (_scheduleContainer != null) _scheduleContainer.style.display = DisplayStyle.None;
            if (_settingsContainer != null) _settingsContainer.style.display = DisplayStyle.None;
            if (_registerTabContainer != null) _registerTabContainer.style.display = DisplayStyle.None; // [FIX] Hide Register

            // SHOW Attendance Container
            if (attendanceContainerRef != null)
            {
                attendanceContainerRef.style.display = DisplayStyle.Flex;
                AnimateEntry(attendanceContainerRef);
            }

            // [NEW] Load Logs
            RefreshMainContent();
            FetchLogsFromFirestore();
        };

        if (btnAttendance != null) btnAttendance.clicked += ShowAttendanceMode;

        // Toggle Camera in Attendance Mode
        if (btnToggleCameraAttendance != null) btnToggleCameraAttendance.clicked += () =>
        {
            TogglePythonScript();
            bool isOn = _isCameraRunning;
            btnToggleCameraAttendance.text = isOn ? "TẮT CAMERA" : "BẬT CAMERA";
            btnToggleCameraAttendance.style.backgroundColor = isOn ? new Color(0.84f, 0.19f, 0.19f) : new Color(0, 0.72f, 0.58f);
        };

        // --- TÍCH HỢP NÚT ADD FACE ---
        _btnAddFace = _uiRoot.Q<Button>("BtnAddFace");
        if (_btnAddFace != null)
        {
            _btnAddFace.clicked -= OnAddFaceClicked;
            _btnAddFace.clicked += OnAddFaceClicked;
        }

        // --- DASHBOARD REDESIGN QUERY ---
        _lblStatTotal = _uiRoot.Q<Label>("LblStatTotal");
        _lblStatPresent = _uiRoot.Q<Label>("LblStatPresent");
        _lblStatAbsent = _uiRoot.Q<Label>("LblStatAbsent");
        _lblStatLate = _uiRoot.Q<Label>("LblStatLate");
        _lblStatPercent = _uiRoot.Q<Label>("LblStatPercent");

        _btnQuickAttendance = _uiRoot.Q<Button>("BtnQuickAttendance");
        if (_btnQuickAttendance != null)
        {
            _btnQuickAttendance.clicked += ShowAttendanceMode;
        }

        _btnQuickAddStudent = _uiRoot.Q<Button>("BtnQuickAddStudent");
        if (_btnQuickAddStudent != null)
        {
            _btnQuickAddStudent.clicked += SwitchToRegisterTab;
        }

        // [NEW] Click handler for Absent card
        var cardAbsent = _uiRoot.Q<VisualElement>("CardAbsent");
        if (cardAbsent != null)
        {
            cardAbsent.RegisterCallback<ClickEvent>(evt => ShowAbsentStudentsPopup());
        }
        // --------------------------------------
        // --------------------------------------
        // -------------------------------------

        // --- CODE CŨ (GIỮ NGUYÊN ĐỂ TƯƠNG THÍCH) ---
        _doc = GetComponent<UIDocument>();
        _root = _doc.rootVisualElement;
        // _root.Clear(); // COMMENT LẠI - UI ĐÃ CÓ TỪ UXML

        // Load Settings
        _lateThreshold = PlayerPrefs.GetString("LateThreshold", "07:00");

        // BuildDashboardLayout(); // <--- QUAN TRỌNG: COMMENT DÒNG NÀY LẠI - UI đã có từ UXML

        LoadHiddenLogIds();

        LoadBackground(); // [FIX] Uncommented to load background

        LoadLogsLocal();

        // RefreshMainContent(); // Comment lại nếu dùng UI mới hoàn toàn

        // --- GỌI HÀM SETUP LIST MỚI ---
        SetupListView();
        SetupStudentListView(); // [NEW setup for Student List]

        // [NEW] Setup Chart Hover Events
        // [NEW] Setup Chart Hover Events
        SetupChartEvents();
        SetupStatsTab(); // [NEW setup Stats]
        SetupScheduleTab(); // [NEW setup Schedule Import & Manual Entry]
        SetupRegisterTab(); // [NEW setup Register Tab UI]
        
        // [NEW] Setup Google Connect Buttons
        SetupConnectGoogleButton();
        SetupOpenGSheetButton();
        SetupGoogleLoginButton(); // [NEW] Setup Login Button

        // [NEW] Style all scrollbars via C# (USS không override được)
        _uiRoot.schedule.Execute(() => StyleAllScrollbars(_uiRoot)).StartingIn(100);

        // [NEW] Fetch logs from server (HTTP) - Only if logged in
        if (_isLoggedIn && !string.IsNullOrEmpty(_currentTeacherId))
        {
            StartCoroutine(FetchLogsFromServer(true));
        }

        // [NEW] Init Dashboard Stats (fetch profile count)
        InitDashboardStats();

        // [FIX] Force Stop Engines on Start to avoid "Could not start graph"
        if (faceDetector != null) faceDetector.gameObject.SetActive(false);
        _isCameraRunning = false;
    }


    void OnDisable()
    {
        // [FIX] Ensure Camera Stop on Disable
        _isCameraRunning = false;
        if (_recognitionCoroutine != null)
        {
            StopCoroutine(_recognitionCoroutine);
            _recognitionCoroutine = null;
        }

        // ✅ FIX: Stop MediaPipe pipeline first to release camera (LED off)
        if (faceDetector != null)
        {
            faceDetector.Stop();
            faceDetector.gameObject.SetActive(false);
        }

        // [New] Cleanup Textures
        if (_capRT != null) { _capRT.Release(); _capRT = null; }
        if (_capTex != null) { Destroy(_capTex); _capTex = null; }

        // [FIX] Save logs on Disable (including Stop Play Mode)
        SaveLogsLocal();
    }

    void OnApplicationQuit()
    {
        // [FIX] Ensure logs are saved on exit
        SaveLogsLocal();
    }

    // [NEW] Kéo ảnh nền vào đây
    [SerializeField] private Texture2D appBackgroundImage;

    // ... (keep other fields if they are in between, but looking at previous file view, they seem close)
    // Actually, I need to be careful about where to insert the field. 
    // Let me check the declaration area first to be precise.

    // ...

    void LoadBackground()
    {
        // [FIX] Apply to .main-container, not _root, because .main-container covers the screen
        var mainContainer = _root.Q<VisualElement>(null, "main-container");
        if (mainContainer == null) mainContainer = _root; // Fallback

        // 1. Ưu tiên ảnh từ Inspector
        if (appBackgroundImage != null)
        {
            mainContainer.style.backgroundImage = new StyleBackground(appBackgroundImage);
            mainContainer.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
            return;
        }

        // 2. Fallback: StreamingAssets
        string[] extensions = { ".jpg", ".png", ".jpeg" };
        string basePath = Path.Combine(Application.streamingAssetsPath, "background");

        foreach (var ext in extensions)
        {
            string path = basePath + ext;
            if (File.Exists(path))
            {
                byte[] fileData = File.ReadAllBytes(path);
                Texture2D tex = new Texture2D(2, 2);
                tex.LoadImage(fileData);
                mainContainer.style.backgroundImage = new StyleBackground(tex);
                mainContainer.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
                return; // Loaded first found image
            }
        }
    }

    // --- PHẦN 1: GIAO DIỆN (UI MAKEOVER) ---
    void BuildDashboardLayout()
    {
        _root.style.flexDirection = FlexDirection.Row;
        _root.style.backgroundColor = colBg;

        // 1. Sidebar (Clean & Modern)
        var sidebar = new VisualElement();
        sidebar.style.width = 260;
        sidebar.style.backgroundColor = colSidebar;
        sidebar.style.paddingTop = 30;
        sidebar.style.paddingBottom = 30;

        // Logo Area
        var logoContainer = new VisualElement();
        logoContainer.style.paddingLeft = 25;
        logoContainer.style.marginBottom = 50;
        var logo = new Label("ATTENDANCE\nMANAGER");
        logo.style.color = Color.white;
        logo.style.fontSize = 22;
        logo.style.unityFontStyleAndWeight = FontStyle.Bold;
        logo.style.letterSpacing = 1;
        logoContainer.Add(logo);
        sidebar.Add(logoContainer);

        // Menu Items
        sidebar.Add(CreateMenuButton("📝  Nhật ký", "All", true));
        sidebar.Add(CreateMenuButton("✅  Đúng giờ", "OnTime", false));
        sidebar.Add(CreateMenuButton("⚠️  Đi trễ", "Late", false));

        var divider = new VisualElement();
        divider.style.height = 1;
        divider.style.backgroundColor = new Color(1, 1, 1, 0.1f);
        divider.style.marginTop = 20;
        divider.style.marginBottom = 20;
        divider.style.marginLeft = 20; divider.style.marginRight = 20;
        sidebar.Add(divider);

        sidebar.Add(CreateMenuButton("📊  Thống kê", "Stats", false));

        // --- CẤU HÌNH GIỜ TRỄ ---
        var lblConfig = new Label("Cấu hình giờ trễ:");
        lblConfig.style.color = new Color(1, 1, 1, 0.7f);
        lblConfig.style.fontSize = 12;
        lblConfig.style.marginLeft = 20; lblConfig.style.marginTop = 20;
        sidebar.Add(lblConfig);

        var txtLateTime = new TextField();
        txtLateTime.value = _lateThreshold;
        txtLateTime.style.marginLeft = 20; txtLateTime.style.marginRight = 20;
        txtLateTime.style.height = 40;
        txtLateTime.RegisterValueChangedCallback(evt =>
        {
            _lateThreshold = evt.newValue;
            PlayerPrefs.SetString("LateThreshold", _lateThreshold);
            PlayerPrefs.Save();
        });
        sidebar.Add(txtLateTime);

        // --- [MỚI] NÚT THÊM KHUÔN MẶT ---
        _btnAddFace = new Button();
        _btnAddFace.text = "➕ Thêm khuôn mặt";
        _btnAddFace.style.height = 45;
        _btnAddFace.style.marginTop = 15;
        _btnAddFace.style.marginLeft = 20; _btnAddFace.style.marginRight = 20;
        // Màu xanh lá đậm để phân biệt
        _btnAddFace.style.backgroundColor = new Color(0.2f, 0.6f, 0.3f);
        _btnAddFace.style.color = Color.white;
        _btnAddFace.style.fontSize = 14;
        _btnAddFace.style.unityFontStyleAndWeight = FontStyle.Bold;
        _btnAddFace.style.borderTopLeftRadius = 10; _btnAddFace.style.borderTopRightRadius = 10;
        _btnAddFace.style.borderBottomLeftRadius = 10; _btnAddFace.style.borderBottomRightRadius = 10;
        _btnAddFace.style.borderTopWidth = 0; _btnAddFace.style.borderBottomWidth = 0;
        _btnAddFace.style.borderLeftWidth = 0; _btnAddFace.style.borderRightWidth = 0;

        _btnAddFace.clicked += OnAddFaceClicked; // Gọi hàm xử lý
        sidebar.Add(_btnAddFace);

        // [NEW] Nút Quản lý Dữ liệu
        var btnManager = new Button();
        btnManager.text = "📂 Quản lý Dữ liệu";
        btnManager.style.height = 45;
        btnManager.style.marginTop = 15;
        btnManager.style.marginLeft = 20; btnManager.style.marginRight = 20;
        btnManager.style.backgroundColor = new Color(0.3f, 0.4f, 0.5f);
        btnManager.style.color = Color.white;
        btnManager.style.fontSize = 14;
        btnManager.style.unityFontStyleAndWeight = FontStyle.Bold;
        btnManager.style.borderTopLeftRadius = 10; btnManager.style.borderTopRightRadius = 10;
        btnManager.style.borderBottomLeftRadius = 10; btnManager.style.borderBottomRightRadius = 10;
        btnManager.style.borderTopWidth = 0; btnManager.style.borderBottomWidth = 0;
        btnManager.style.borderLeftWidth = 0; btnManager.style.borderRightWidth = 0;

        btnManager.clicked += () => ShowFaceDataManager(); // [NEW] Call Manager UI
        sidebar.Add(btnManager);

        // [NEW] Nút Phục hồi ẩn
        var btnRestore = new Button();
        btnRestore.text = "♻ Phục hồi ẩn";
        btnRestore.style.height = 45;
        btnRestore.style.marginTop = 15;
        btnRestore.style.marginLeft = 20; btnRestore.style.marginRight = 20;
        btnRestore.style.backgroundColor = new Color(0.4f, 0.4f, 0.45f);
        btnRestore.style.color = Color.white;
        btnRestore.style.fontSize = 14;
        btnRestore.style.unityFontStyleAndWeight = FontStyle.Bold;
        btnRestore.style.borderTopLeftRadius = 10; btnRestore.style.borderTopRightRadius = 10;
        btnRestore.style.borderBottomLeftRadius = 10; btnRestore.style.borderBottomRightRadius = 10;
        btnRestore.style.borderTopWidth = 0; btnRestore.style.borderBottomWidth = 0;
        btnRestore.style.borderLeftWidth = 0; btnRestore.style.borderRightWidth = 0;

        btnRestore.clicked += ClearHiddenLogs;
        sidebar.Add(btnRestore);

        _btnCamera = new Button();

        // --- NÚT CAMERA ---
        _btnCamera = new Button();
        _btnCamera.text = "📷 Bật Camera AI";
        _btnCamera.style.height = 45;
        _btnCamera.style.marginTop = 15;
        _btnCamera.style.marginLeft = 20; _btnCamera.style.marginRight = 20;
        _btnCamera.style.backgroundColor = new Color(0.25f, 0.3f, 0.4f);
        _btnCamera.style.color = Color.white;
        _btnCamera.style.fontSize = 14;
        _btnCamera.style.unityFontStyleAndWeight = FontStyle.Bold;
        _btnCamera.style.borderTopLeftRadius = 10; _btnCamera.style.borderTopRightRadius = 10;
        _btnCamera.style.borderBottomLeftRadius = 10; _btnCamera.style.borderBottomRightRadius = 10;
        _btnCamera.style.borderTopWidth = 0; _btnCamera.style.borderBottomWidth = 0;
        _btnCamera.style.borderLeftWidth = 0; _btnCamera.style.borderRightWidth = 0;

        _btnCamera.clicked += TogglePythonScript;
        sidebar.Add(_btnCamera);

        // --- CAMERA PREVIEW (SIDEBAR) ---
        _sidebarCameraPreview = new VisualElement();
        _sidebarCameraPreview.style.width = 220;
        _sidebarCameraPreview.style.height = 165;
        _sidebarCameraPreview.style.marginTop = 10;
        _sidebarCameraPreview.style.marginLeft = 20;
        _sidebarCameraPreview.style.backgroundColor = Color.black;
        _sidebarCameraPreview.style.display = DisplayStyle.None; // Ẩn mặc định
        _sidebarCameraPreview.style.borderTopLeftRadius = 8;
        _sidebarCameraPreview.style.borderTopRightRadius = 8;
        _sidebarCameraPreview.style.borderBottomLeftRadius = 8;
        _sidebarCameraPreview.style.borderBottomRightRadius = 8;
        sidebar.Add(_sidebarCameraPreview);


        _root.Add(sidebar);


        // 2. Main Content
        var content = new VisualElement();
        content.style.flexGrow = 1;
        content.style.paddingTop = 40; content.style.paddingLeft = 50; content.style.paddingRight = 50; content.style.paddingBottom = 40;

        // --- TOP BAR ---
        var topBar = new VisualElement();
        topBar.style.flexDirection = FlexDirection.Row;
        topBar.style.justifyContent = Justify.SpaceBetween;
        topBar.style.alignItems = Align.Center;
        topBar.style.marginBottom = 35;

        var titleGroup = new VisualElement();
        var title = new Label("HCMUTE");
        title.style.fontSize = 32;
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        title.style.color = colTextDark;
        titleGroup.Add(title);

        var dateLbl = new Label(DateTime.Now.ToString("dddd, dd MMMM yyyy"));
        dateLbl.style.color = colTextLight;
        dateLbl.style.fontSize = 14;
        titleGroup.Add(dateLbl);

        topBar.Add(titleGroup);

        // Right side of Top Bar
        var topControls = new VisualElement();
        topControls.style.flexDirection = FlexDirection.Row;
        topControls.style.alignItems = Align.Center;

        _lblClock = new Label("00:00:00");
        _lblClock.style.fontSize = 20;
        _lblClock.style.unityFontStyleAndWeight = FontStyle.Bold;
        _lblClock.style.color = colAccent;
        _lblClock.style.marginRight = 30;
        topControls.Add(_lblClock);

        // Search Box (Pill Shape)
        _searchInput = new TextField();
        _searchInput.RegisterValueChangedCallback(evt =>
        {
            _searchText = evt.newValue.ToLower();
            RefreshMainContent();
        });
        _searchInput.style.width = 320;
        _searchInput.style.height = 45;

        var searchInputText = _searchInput.Q(TextField.textInputUssName);
        if (searchInputText != null)
        {
            searchInputText.style.backgroundColor = Color.white;
            searchInputText.style.borderTopLeftRadius = 25; searchInputText.style.borderTopRightRadius = 25;
            searchInputText.style.borderBottomLeftRadius = 25; searchInputText.style.borderBottomRightRadius = 25;

            searchInputText.style.borderTopWidth = 1; searchInputText.style.borderBottomWidth = 1;
            searchInputText.style.borderLeftWidth = 1; searchInputText.style.borderRightWidth = 1;

            Color borderColor = new Color(0.9f, 0.9f, 0.95f);
            searchInputText.style.borderTopColor = borderColor; searchInputText.style.borderBottomColor = borderColor;
            searchInputText.style.borderLeftColor = borderColor; searchInputText.style.borderRightColor = borderColor;

            searchInputText.style.paddingLeft = 20;
            searchInputText.style.fontSize = 14;
        }
        _searchInput.value = "";
        topControls.Add(_searchInput);

        // --- BỘ LỌC NGÀY (Dropdown đơn giản) ---
        var dateFilter = new DropdownField(new List<string> { "Tất cả", "Hôm nay", "Hôm qua" }, 0);
        dateFilter.style.width = 120;
        dateFilter.style.height = 45;
        dateFilter.style.marginLeft = 10;
        dateFilter.RegisterValueChangedCallback(evt =>
        {
            if (evt.newValue == "Tất cả") _dateFilterMode = "All";
            else if (evt.newValue == "Hôm nay") _dateFilterMode = "Today";
            else if (evt.newValue == "Hôm qua") _dateFilterMode = "Yesterday";
            RefreshMainContent();
        });
        topControls.Add(dateFilter);

        topBar.Add(topControls);
        content.Add(topBar);

        // --- DATA TOOLBAR (SIMPLIFIED: Chỉ còn Export) ---
        var toolsRow = new VisualElement();
        toolsRow.style.flexDirection = FlexDirection.Row;
        toolsRow.style.justifyContent = Justify.FlexEnd; // Đẩy sang phải
        toolsRow.style.marginBottom = 20;

        // Export Button
        var btnExport = new Button();
        btnExport.text = "📥 Xuất Báo Cáo (CSV)";
        btnExport.style.height = 35;
        btnExport.style.width = 200;
        btnExport.style.backgroundColor = new Color(0.2f, 0.6f, 0.3f); // Green
        btnExport.style.color = Color.white;
        btnExport.style.unityFontStyleAndWeight = FontStyle.Bold;
        btnExport.style.borderTopLeftRadius = 8; btnExport.style.borderTopRightRadius = 8;
        btnExport.style.borderBottomLeftRadius = 8; btnExport.style.borderBottomRightRadius = 8;
        btnExport.style.borderTopWidth = 0; btnExport.style.borderBottomWidth = 0;
        btnExport.style.borderLeftWidth = 0; btnExport.style.borderRightWidth = 0;
        btnExport.clicked += ExportToCSV;
        toolsRow.Add(btnExport);

        content.Add(toolsRow);

        // --- TOAST CONTAINER (Overlay) ---
        _toastContainer = new VisualElement();
        _toastContainer.style.position = Position.Absolute;
        _toastContainer.style.top = 20;
        // P1 FIX: Center Toast
        _toastContainer.style.left = 0; _toastContainer.style.right = 0;
        _toastContainer.style.alignItems = Align.Center;
        _root.Add(_toastContainer);

        // --- STATS ROW (New Design) ---
        var statsRow = new VisualElement();
        statsRow.style.flexDirection = FlexDirection.Row;
        statsRow.style.marginBottom = 30;
        statsRow.style.height = 100;

        // Stat Cards...
        _lblTotal = CreateStatCard(statsRow, "Lượt check-in", "0", Color.white, colTextDark);
        // ... (Other cards created via method, assuming they are appended to statsRow)
        // NOTE: We need to make sure CreateStatCard appends to statsRow. 
        // Based on previous code, it takes parent as arg.

        _lblOnTime = CreateStatCard(statsRow, "Đúng giờ", "0", colGreen, Color.white);
        _lblLate = CreateStatCard(statsRow, "Đi trễ", "0", colRed, Color.white);
        content.Add(statsRow);


        // --- [MỚI] CAMERA MAIN CONTAINER (CENTER) ---
        _cameraMainContainer = new VisualElement();
        _cameraMainContainer.style.width = 640;
        _cameraMainContainer.style.height = 480;
        _cameraMainContainer.style.alignSelf = Align.Center; // Căn giữa
        _cameraMainContainer.style.backgroundColor = Color.black;
        _cameraMainContainer.style.marginBottom = 20;
        _cameraMainContainer.style.borderTopLeftRadius = 12; _cameraMainContainer.style.borderTopRightRadius = 12;
        _cameraMainContainer.style.borderBottomLeftRadius = 12; _cameraMainContainer.style.borderBottomRightRadius = 12;
        _cameraMainContainer.style.display = DisplayStyle.None; // Ẩn mặc định

        // --- P2: MIRROR CONTAINER (Flip Everything for Selfie Mode) ---
        _mirrorContainer = new VisualElement();
        _mirrorContainer.style.width = Length.Percent(100);
        _mirrorContainer.style.height = Length.Percent(100);
        _mirrorContainer.style.scale = new Scale(new Vector3(-1, 1, 1)); // FLIP X
        _cameraMainContainer.Add(_mirrorContainer);

        // Label overlay (OUTSIDE mirror to read normally)
        _camLabel = new Label("CAMERA AI ĐANG CHẠY...");
        _camLabel.style.position = Position.Absolute;
        _camLabel.style.bottom = 10; _camLabel.style.right = 10;

        // [NEW] Style updates: To, Rõ, Đậm for percentage
        _camLabel.style.color = new Color(1, 1, 1, 1f);
        _camLabel.style.fontSize = 20;
        _camLabel.style.unityFontStyleAndWeight = FontStyle.Bold;

        _cameraMainContainer.Add(_camLabel);

        // --- FACE TRACKING BOX (INSIDE MIRROR) ---
        _faceTrackingBox = new VisualElement();
        _faceTrackingBox.style.position = Position.Absolute;
        _faceTrackingBox.style.borderTopWidth = 2; _faceTrackingBox.style.borderBottomWidth = 2;
        _faceTrackingBox.style.borderLeftWidth = 2; _faceTrackingBox.style.borderRightWidth = 2;
        _faceTrackingBox.style.borderTopColor = colGreen; _faceTrackingBox.style.borderBottomColor = colGreen;
        _faceTrackingBox.style.borderLeftColor = colGreen; _faceTrackingBox.style.borderRightColor = colGreen;
        _faceTrackingBox.style.display = DisplayStyle.None;
        _mirrorContainer.Add(_faceTrackingBox); // Add to Mirror

        // --- FACE INFO LABEL (Attached to Box) ---
        // --- FACE INFO LABEL (Attached to Box) ---
        _faceInfoLabel = new Label("Detecting...");
        _faceInfoLabel.style.position = Position.Absolute;

        // Vị trí (đang top=-30,left=0) -> -42 để khỏi bị che
        _faceInfoLabel.style.top = -42;
        _faceInfoLabel.style.left = 0;

        _faceInfoLabel.style.backgroundColor = new Color(0, 0, 0, 0.85f);   // tối hơn
        _faceInfoLabel.style.color = Color.white;                             // trắng cho dễ đọc
        _faceInfoLabel.style.fontSize = 20;                                   // TO hơn (14 -> 20)
        _faceInfoLabel.style.unityFontStyleAndWeight = FontStyle.Bold;

        _faceInfoLabel.style.paddingLeft = 10; _faceInfoLabel.style.paddingRight = 10;
        _faceInfoLabel.style.paddingTop = 6; _faceInfoLabel.style.paddingBottom = 6;

        // bo góc
        _faceInfoLabel.style.borderTopLeftRadius = 8;
        _faceInfoLabel.style.borderTopRightRadius = 8;
        _faceInfoLabel.style.borderBottomLeftRadius = 8;
        _faceInfoLabel.style.borderBottomRightRadius = 8;

        // viền chữ (rất hiệu quả để đọc trên nền camera)
        _faceInfoLabel.style.unityTextOutlineColor = Color.black;
        _faceInfoLabel.style.unityTextOutlineWidth = 1;

        // COUNTER FLIP TEXT giữ nguyên để khỏi bị ngược
        _faceInfoLabel.style.scale = new Scale(new Vector3(-1, 1, 1));

        _faceTrackingBox.Add(_faceInfoLabel);
        content.Add(_cameraMainContainer);



        // Progress Bar (Slimmer)
        var progressContainer = new VisualElement();
        progressContainer.style.height = 6;
        progressContainer.style.flexDirection = FlexDirection.Row;
        progressContainer.style.backgroundColor = new Color(0.9f, 0.9f, 0.95f);
        progressContainer.style.borderTopLeftRadius = 3; progressContainer.style.borderTopRightRadius = 3;
        progressContainer.style.borderBottomLeftRadius = 3; progressContainer.style.borderBottomRightRadius = 3;
        progressContainer.style.marginBottom = 30;
        progressContainer.style.overflow = Overflow.Hidden;

        var barOnTime = new VisualElement();
        barOnTime.name = "barOnTime";
        barOnTime.style.backgroundColor = colGreen;
        barOnTime.style.height = Length.Percent(100);
        barOnTime.style.width = Length.Percent(0);
        barOnTime.style.transitionProperty = new List<StylePropertyName> { new StylePropertyName("width") };
        barOnTime.style.transitionDuration = new List<TimeValue> { new TimeValue(0.5f) };

        var barLate = new VisualElement();
        barLate.name = "barLate";
        barLate.style.backgroundColor = colRed;
        barLate.style.height = Length.Percent(100);
        barLate.style.width = Length.Percent(0);
        barLate.style.transitionProperty = new List<StylePropertyName> { new StylePropertyName("width") };
        barLate.style.transitionDuration = new List<TimeValue> { new TimeValue(0.5f) };

        progressContainer.Add(barOnTime);
        progressContainer.Add(barLate);
        content.Add(progressContainer);

        // List Title
        _lblListTitle = new Label("Danh sách điểm danh");
        _lblListTitle.style.fontSize = 20;
        _lblListTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
        _lblListTitle.style.color = colTextDark;
        _lblListTitle.style.marginBottom = 20;
        content.Add(_lblListTitle);

        // Header Row (For Stats View)
        _statsHeaderRow = new VisualElement();
        _statsHeaderRow.style.flexDirection = FlexDirection.Row;
        _statsHeaderRow.style.backgroundColor = Color.white;
        _statsHeaderRow.style.height = 50;
        _statsHeaderRow.style.alignItems = Align.Center;
        _statsHeaderRow.style.paddingLeft = 25;
        _statsHeaderRow.style.paddingRight = 25;
        _statsHeaderRow.style.borderTopLeftRadius = 15;
        _statsHeaderRow.style.borderTopRightRadius = 15;
        _statsHeaderRow.style.marginBottom = 2;
        _statsHeaderRow.style.display = DisplayStyle.None;

        _statsHeaderRow.Add(new Label("Sinh viên") { style = { width = Length.Percent(35), unityFontStyleAndWeight = FontStyle.Bold, color = colTextLight } });
        _statsHeaderRow.Add(new Label("MSSV") { style = { width = Length.Percent(20), unityFontStyleAndWeight = FontStyle.Bold, color = colTextLight } });
        _statsHeaderRow.Add(new Label("Đúng giờ") { style = { width = Length.Percent(15), unityFontStyleAndWeight = FontStyle.Bold, color = colGreen } });
        _statsHeaderRow.Add(new Label("Trễ") { style = { width = Length.Percent(15), unityFontStyleAndWeight = FontStyle.Bold, color = colRed } });
        content.Add(_statsHeaderRow);

        // ScrollView Container
        var scrollView = new ScrollView();
        scrollView.style.flexGrow = 1;
        _listContainer = new VisualElement();
        scrollView.Add(_listContainer);

        content.Add(scrollView);
        _root.Add(content);
        CreateSplashScreen();
    }

    VisualElement CreateMenuButton(string text, string viewMode, bool isActive)
    {
        var btn = new Button();
        btn.name = "btn_" + viewMode;
        btn.style.height = 50;
        btn.style.flexDirection = FlexDirection.Row;
        btn.style.alignItems = Align.Center;
        btn.style.backgroundColor = Color.clear; // Reset default
        btn.style.borderTopWidth = 0; btn.style.borderBottomWidth = 0;
        btn.style.borderLeftWidth = 0; btn.style.borderRightWidth = 0;
        btn.style.marginBottom = 5;

        // Active Indicator (Bar)
        var indicator = new VisualElement();
        indicator.name = "indicator";
        indicator.style.width = 4;
        indicator.style.height = 24;
        indicator.style.backgroundColor = isActive ? colAccent : Color.clear;
        indicator.style.borderTopRightRadius = 2;
        indicator.style.borderBottomRightRadius = 2;
        indicator.style.marginRight = 15;
        btn.Add(indicator);

        // Text
        var lbl = new Label(text);
        lbl.style.color = isActive ? Color.white : new Color(1, 1, 1, 0.6f);
        lbl.style.fontSize = 15;
        lbl.style.unityTextAlign = TextAnchor.MiddleLeft;
        btn.Add(lbl);

        // Logic hover
        btn.RegisterCallback<MouseEnterEvent>(evt =>
        {
            if (_currentViewMode != viewMode)
                lbl.style.color = Color.white;
        });
        btn.RegisterCallback<MouseLeaveEvent>(evt =>
        {
            if (_currentViewMode != viewMode)
                lbl.style.color = new Color(1, 1, 1, 0.6f);
        });

        btn.RegisterCallback<ClickEvent>(evt =>
        {
            _currentViewMode = viewMode;
            UpdateMenuButtonsState();
            RefreshMainContent();
        });
        return btn;
    }

    void UpdateMenuButtonsState()
    {
        string[] modes = { "All", "OnTime", "Late", "Stats", "Absent" };
        foreach (var m in modes)
        {
            var btn = _root.Q<Button>("btn_" + m);
            if (btn != null)
            {
                var indicator = btn.Q("indicator");
                var lbl = btn.Q<Label>();
                bool isActive = (_currentViewMode == m);

                if (indicator != null) indicator.style.backgroundColor = isActive ? colAccent : Color.clear;
                if (lbl != null) lbl.style.color = isActive ? Color.white : new Color(1, 1, 1, 0.6f);
            }
        }
    }

    Label CreateStatCard(VisualElement parent, string title, string value, Color bg, Color txtColor)
    {
        var card = new VisualElement();
        card.style.flexGrow = 1;
        card.style.backgroundColor = bg;
        card.style.marginRight = 20;
        card.style.paddingTop = 20; card.style.paddingLeft = 25;
        card.style.borderTopLeftRadius = 15; card.style.borderTopRightRadius = 15;
        card.style.borderBottomLeftRadius = 15; card.style.borderBottomRightRadius = 15;

        // Shadow Fake
        card.style.borderBottomWidth = 4;
        card.style.borderBottomColor = new Color(0.9f, 0.9f, 0.93f);

        var lblTitle = new Label(title);
        lblTitle.style.color = new Color(0.5f, 0.5f, 0.6f);
        lblTitle.style.fontSize = 14;
        lblTitle.style.marginBottom = 5;

        var lblValue = new Label(value);
        lblValue.style.fontSize = 36;
        lblValue.style.unityFontStyleAndWeight = FontStyle.Bold;
        lblValue.style.color = txtColor;
        // P3 FIX: Center Align
        lblValue.style.unityTextAlign = TextAnchor.MiddleCenter;
        lblValue.style.width = Length.Percent(100);

        card.Add(lblTitle);
        card.Add(lblValue);
        parent.Add(card);
        return lblValue;
    }

    void ShowToast(string message, Color color)
    {
        if (_toastContainer == null) return;

        var toast = new Label(message);
        toast.style.backgroundColor = color;
        toast.style.color = Color.white;
        toast.style.paddingTop = 10; toast.style.paddingBottom = 10;
        toast.style.paddingLeft = 20; toast.style.paddingRight = 20;
        toast.style.marginBottom = 10;
        toast.style.borderTopLeftRadius = 5; toast.style.borderTopRightRadius = 5;
        toast.style.borderBottomLeftRadius = 5; toast.style.borderBottomRightRadius = 5;
        toast.style.fontSize = 14;
        toast.style.unityFontStyleAndWeight = FontStyle.Bold;

        // Animation In
        toast.style.opacity = 0;
        toast.style.translate = new Translate(new Length(50, LengthUnit.Pixel), new Length(0, LengthUnit.Pixel), 0);
        toast.style.transitionProperty = new List<StylePropertyName> { new StylePropertyName("opacity"), new StylePropertyName("translate") };
        toast.style.transitionDuration = new List<TimeValue> { new TimeValue(0.3f) };

        _toastContainer.Add(toast);

        // Trigger anim
        toast.schedule.Execute(() =>
        {
            toast.style.opacity = 1;
            toast.style.translate = new Translate(new Length(0, LengthUnit.Pixel), new Length(0, LengthUnit.Pixel), 0);
        }).StartingIn(50);

        // Remove after 3s
        toast.schedule.Execute(() =>
        {
            toast.style.opacity = 0;
            toast.schedule.Execute(() => _toastContainer.Remove(toast)).StartingIn(300); // Wait for fade out
        }).StartingIn(3000);
    }

    // --- NEW: FETCH LOGS HTTP ---
    private static long EpochMsToTicks(long ms)
    {
        try { return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.Ticks; }
        catch { return DateTime.Now.Ticks; }
    }

    private string ComputeStatusByTime(string timeStr, string rawStatus)
    {
        string status = string.IsNullOrEmpty(rawStatus) ? "Present" : rawStatus;
        if (status == "Absent" || status == "Vắng") return status;

        if (TimeSpan.TryParse(timeStr, out var logTime) &&
            TimeSpan.TryParse(_lateThreshold, out var threshold) &&
            logTime > threshold)
            return "Late";
        return "OnTime";
    }

    private IEnumerator FetchLogsFromServer(bool silent = false)
    {
        if (_logsInFlight) yield break;
        _logsInFlight = true;
        int ctx = _tenantContextVersion; // ✅ capture context to detect class switch during fetch

        string url = cloudRunApiUrl.TrimEnd('/') + $"/logs?limit={LOG_FETCH_LIMIT}";
        using (var req = UnityWebRequest.Get(url))
        {
            ApplyTenantHeaders(req);
            req.timeout = 120; // Increased for RetinaFace cold start
            yield return req.SendWebRequest();

            // ✅ nếu trong lúc fetch bạn đã đổi lớp/term -> bỏ kết quả để tránh đè sai lớp
            if (ctx != _tenantContextVersion)
            {
                _logsInFlight = false;
                yield break;
            }

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("GET /logs failed: " + req.error + " | " + req.downloadHandler.text);
                if (!silent) ShowToast("❌ Không tải được nhật ký!", colRed);
                _logsInFlight = false;
                yield break;
            }

            var resp = JsonUtility.FromJson<LogsResponse>(req.downloadHandler.text);
            if (resp != null && resp.logs != null)
            {
                var map = new Dictionary<string, AttendanceLog>();

                // keep existing logs if needed
                foreach (var l in _allLogs)
                    if (!string.IsNullOrEmpty(l.id)) map[l.id] = l;

                foreach (var it in resp.logs)
                {
                    if (it == null || string.IsNullOrEmpty(it.id)) continue;
                    if (_hiddenLogIds != null && _hiddenLogIds.Contains(it.id)) continue;

                    // [FIX] Ensure we don't overwrite local manual logs that haven't synced yet
                    if (map.ContainsKey(it.id))
                    {
                        var localLog = map[it.id];
                        if (!localLog.isSynced)
                        {
                            // Debug.Log($"[Sync] Keeping local log {it.id} because it is not synced yet.");
                            continue;
                        }
                    }

                    string t = string.IsNullOrEmpty(it.time) ? DateTime.Now.ToString("HH:mm:ss") : it.time;

                    // [FIX] Calculate date/timestamp correctly even if vn_epoch_ms is 0 (Legacy Server)
                    long ts = it.vn_epoch_ms;
                    string d = it.date;

                    if (ts <= 0)
                    {
                        // Fallback: Parse date string "dd/MM/yyyy" + "HH:mm:ss"
                        if (!string.IsNullOrEmpty(it.date) && !string.IsNullOrEmpty(it.time))
                        {
                            try
                            {
                                // Assume Server sends local time strings
                                DateTime dt = DateTime.ParseExact($"{it.date} {it.time}", "dd/MM/yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                                ts = ((DateTimeOffset)dt).ToUnixTimeMilliseconds();
                            }
                            catch { /* Ignore, use Now */ }
                        }

                        if (ts <= 0) ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    }

                    if (string.IsNullOrEmpty(d))
                    {
                        d = DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime().ToString("dd/MM/yyyy");
                    }
                    else
                    {
                        // [FIX] Normalize yyyy-MM-dd to dd/MM/yyyy to match local filter
                        if (d.Contains("-") && d.Length == 10) // Simple check for yyyy-MM-dd
                        {
                            if (DateTime.TryParseExact(d, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out DateTime dtServer))
                            {
                                d = dtServer.ToString("dd/MM/yyyy");
                            }
                        }
                    }

                    var log = new AttendanceLog
                    {
                        id = it.id,
                        studentName = it.name,
                        mssv = it.mssv,
                        time = t,
                        date = d, // [NEW] Bind date
                        status = ComputeStatusByTime(t, it.status),
                        confidence = it.match_percent,
                        timestamp = EpochMsToTicks(ts)
                    };
                    map[it.id] = log;
                }

                _allLogs = map.Values.OrderByDescending(x => x.timestamp).ToList();
                Debug.Log($"[DEBUG] Fetched {_allLogs.Count} logs from server. Raw response count: {resp.logs?.Length ?? 0}");
            }

            UpdateTopStats();
            RefreshMainContent();
            SaveLogsLocal();

            // [FIX] Update Dashboard Stats and Absent List now that we have fresh logs
            UpdateStatsFromLogs();
            FetchAndRenderStudentList();

            // ✅ update all charts
            UpdateTrendChart();
            UpdateStatsTab();

            Debug.Log($"[DEBUG] After refresh, _allLogs.Count = {_allLogs.Count}");
            if (!silent) ShowToast("✅ Đã đồng bộ nhật ký!", colGreen);
        }

        _logsInFlight = false;
    }

    // --- FETCH LOGS VIA CLOUD RUN API (reads from Firestore on server) ---
    private void FetchLogsFromFirestore()
    {
        // Use existing Cloud Run API which reads from Firestore
        ShowToast("🔄 Đang đồng bộ từ Server...", colAccent);
        StartCoroutine(FetchLogsFromServer(false));
    }



    void Update()
    {
        UpdateFaceBBox();
        bool needRefresh = false;

        lock (_lock)
        {
            // 1. Thêm mới
            while (_incomingQueue.Count > 0)
            {
                var newLog = _incomingQueue.Dequeue();
                _allLogs.Insert(0, newLog);
                needRefresh = true;

                // Play Sound & Toast (Chỉ nếu log mới xuất hiện trong vòng 5s gần đây để tránh spam khi load lại app)
                // Lưu ý: timestamp ở đây là tick lúc nhận data
                if (DateTime.Now.Ticks - newLog.timestamp < 5 * 10000000)
                {
                    ShowToast($"✅ {newLog.studentName} đã điểm danh!", colGreen);
                }
            }

            // 2. Xử lý xóa
            if (_deletedIds.Count > 0)
            {
                foreach (var id in _deletedIds)
                {
                    _allLogs.RemoveAll(x => x.id == id);
                }
                _deletedIds.Clear();
                needRefresh = true;
            }
        }

        if (needRefresh)
        {
            UpdateTopStats();
            RefreshMainContent();

            // P0 FIX: Save Changes (From Firebase or general update)
            SaveLogsLocal();
        }

        if (_lblClock != null)
        {
            _lblClock.text = DateTime.Now.ToString("HH:mm:ss");
        }

        // --- CẬP NHẬT ĐỒNG HỒ CHO UI MỚI ---
        if (_uiLblClock != null)
        {
            _uiLblClock.text = DateTime.Now.ToString("HH:mm:ss");
        }

        // [NEW] Cập nhật header date/week (theo đợt học kỳ)
        UpdateHeaderDateWeek(force: false);
    }


    void DeleteLog(string logId)
    {
        if (string.IsNullOrEmpty(logId)) return;

        // 1. Remove Local First (UI Instant Update)
        int removedCount = _allLogs.RemoveAll(x => x.id == logId);

        if (removedCount > 0)
        {
            SaveLogsLocal();
            UpdateTopStats();
            RefreshMainContent(); // Re-render UI
            Debug.Log($"Đã xóa record local: {logId}");

            StartCoroutine(DeleteLogApi(logId));
        }
    }

    IEnumerator DeleteLogApi(string logId)
    {
        string url = cloudRunApiUrl.TrimEnd('/') + "/delete_log";
        // Manual JSON for simplicity
        string json = $"{{\"log_id\":\"{logId}\"}}";
        byte[] body = Encoding.UTF8.GetBytes(json);

        using (var req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            ApplyTenantHeaders(req);

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[DeleteLog] Remote failed: {req.error}");
            }
            else
            {
                Debug.Log($"[DeleteLog] Remote OK: {req.downloadHandler.text}");
            }
        }
    }

    void UpdateTopStats()
    {
        // ================== OPTION A (Teacher view) ==================
        // Nếu giáo viên đang ở Trang chủ và chọn "xem ngày" khác,
        // thì giữ số liệu theo ngày đang xem (UpdateStatsFromLogs),
        // không để logic "today session" ghi đè liên tục.
        if (_uiRoot != null && _drpDaySelector != null)
        {
            var home = _uiRoot.Q<VisualElement>("MainContentContainer");
            bool homeVisible = home != null && home.resolvedStyle.display != DisplayStyle.None;
            if (homeVisible)
            {
                string daySel = (_drpDaySelector.value ?? "").Trim();
                if (!string.IsNullOrEmpty(daySel) && daySel != "Hôm nay")
                    return;
            }
        }

        int totalStudents = _studentDataList?.Count ?? 0;

        if (_lblStatTotal != null) _lblStatTotal.text = totalStudents.ToString();

        int late = 0;
        int totalAbsent = 0;
        int totalPresent = 0;
        string absentText = "0"; // "--" khi chưa chốt vắng theo OPTION A

        // Không có roster thì khỏi tính
        if (totalStudents <= 0)
        {
            if (_lblStatLate != null) _lblStatLate.text = "0";
            if (_lblStatAbsent != null) _lblStatAbsent.text = "0";
            if (_lblStatPercent != null) _lblStatPercent.text = "0%";
            UpdateChart(_allLogs);
            return;
        }

        DateTime now = DateTime.Now;
        DateTime today = DateTime.Today;

        // Nếu chưa có lịch dạy => OPTION A: không suy ra vắng cho "hôm nay"
        if (_scheduleList == null || _scheduleList.Count == 0)
        {
            var todayLogs = _allLogs.Where(log =>
            {
                if (string.IsNullOrEmpty(log.date)) return false;
                if (DateTime.TryParseExact(log.date, "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out DateTime dt))
                    return dt.Date == today;
                return false;
            }).ToList();

            totalPresent = todayLogs.Select(x => x.mssv).Distinct().Count();
            late = todayLogs.Count(x => x.status == "Late");
            totalAbsent = 0;
            absentText = "--"; // Không có TKB => không dám chốt vắng
        }
        else
        {
            // Session-based: tìm buổi đang diễn ra hoặc gần nhất đã kết thúc
            var occOpt = FindHomeSessionOccurrence(today, now);

            // Chưa tới giờ học hôm nay => vắng = 0
            if (!occOpt.HasValue)
            {
                if (_lblStatLate != null) _lblStatLate.text = "0";
                if (_lblStatAbsent != null) _lblStatAbsent.text = "0";
                if (_lblStatPercent != null) _lblStatPercent.text = "0%";
                UpdateChart(_allLogs);
                return;
            }

            var occ = occOpt.Value;
            var earliest = GetEarliestCheckinByStudent(occ);

            totalPresent = earliest.Count;

            // ✅ OPTION A: Chỉ chốt vắng khi buổi đã kết thúc
            if (occ.End <= now)
            {
                totalAbsent = Math.Max(0, totalStudents - totalPresent);
                absentText = totalAbsent.ToString();
            }
            else
            {
                totalAbsent = 0;
                absentText = "--"; // Buổi chưa kết thúc
            }

            // Tính trễ theo start buổi + grace phút
            DateTime lateCut = occ.Start.AddMinutes(LATE_GRACE_MINUTES);
            foreach (var dt in earliest.Values)
                if (dt > lateCut) late++;
        }

        // --- CẬP NHẬT UI MỚI (DASHBOARD) ---
        if (_lblStatAbsent != null) _lblStatAbsent.text = absentText;
        if (_lblStatLate != null) _lblStatLate.text = late.ToString();

        if (_lblStatPercent != null && totalStudents > 0)
        {
            float percent = (float)totalPresent / totalStudents * 100f;
            _lblStatPercent.text = $"{percent:F0}%";
        }

        // --- CẬP NHẬT UI CŨ (Giữ tương thích) ---
        if (_lblTotal != null) _lblTotal.text = $"{totalPresent}";
        if (_lblLate != null) _lblLate.text = $"{late}";
        int onTime = Math.Max(0, totalPresent - late);
        if (_lblOnTime != null) _lblOnTime.text = $"{onTime}";

        if (totalPresent > 0)
        {
            float onTimePercent = (float)onTime / totalPresent * 100f;
            float latePercent = (float)late / totalPresent * 100f;

            var barGreen = _root?.Q("barOnTime");
            var barRed = _root?.Q("barLate");

            if (barGreen != null) barGreen.style.width = Length.Percent(onTimePercent);
            if (barRed != null) barRed.style.width = Length.Percent(latePercent);
        }

        // Chart always shows history (Week)
        UpdateChart(_allLogs);
    }



    // --- [NEW] CHART LOGIC ---
    void UpdateChart(List<AttendanceLog> logs) // Make sure to call this in UpdateTopStats or FetchLogs
    {
        // 1. Find chart container
        var chartContainer = _uiRoot.Q<VisualElement>("TrendChartCanvas");
        if (chartContainer == null)
        {
            chartContainer = _uiRoot.Q<VisualElement>(className: "chart-container");
        }
        if (chartContainer == null) return;

        // 2. Clear and setup container
        chartContainer.Clear();
        chartContainer.style.flexDirection = FlexDirection.Row;
        chartContainer.style.justifyContent = Justify.SpaceBetween;
        chartContainer.style.alignItems = Align.FlexEnd;
        chartContainer.style.paddingLeft = 10;
        chartContainer.style.paddingRight = 10;
        chartContainer.style.paddingBottom = 5;

        // 3. Get week dates
        DateTime today = DateTime.Now;
        int dow = (int)today.DayOfWeek;
        if (dow == 0) dow = 7;
        DateTime monday = today.AddDays(-(dow - 1));

        // 4. Load roster
        if (_studentDataList == null) _studentDataList = new List<StudentStat>();
        if (_studentDataList.Count == 0) LoadStudentDataListFromFile();
        int totalStudents = Math.Max(1, _studentDataList.Count);

        // 5. Create 7 bars
        string[] labels = { "T2", "T3", "T4", "T5", "T6", "T7", "CN" };
        int maxBarHeight = 120; // pixels

        for (int i = 0; i < 7; i++)
        {
            var dayDate = monday.AddDays(i).Date;

            // Count attendance
            int onTime = 0, late = 0;
            foreach (var log in logs)
            {
                if (string.IsNullOrEmpty(log.date)) continue;
                if (DateTime.TryParseExact(log.date, "dd/MM/yyyy",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out DateTime dt))
                {
                    if (dt.Date == dayDate)
                    {
                        if (log.status == "Late") late++;
                        else onTime++;
                    }
                }
            }

            int present = onTime + late;
            int absent = Math.Max(0, totalStudents - present);

            // Create column container
            var col = new VisualElement();
            col.style.flexDirection = FlexDirection.Column;
            col.style.alignItems = Align.Center;
            col.style.width = 35;

            // Create stacked bar container (fixed height area)
            var barArea = new VisualElement();
            barArea.style.height = maxBarHeight;
            barArea.style.width = 30;
            barArea.style.flexDirection = FlexDirection.Column;
            barArea.style.justifyContent = Justify.FlexEnd;
            barArea.style.alignItems = Align.Center;

            if (present > 0 || absent > 0)
            {
                // Calculate heights based on count (not percentage)
                float scale = (float)maxBarHeight / totalStudents;
                int absentH = (int)(absent * scale);
                int lateH = (int)(late * scale);
                int onTimeH = (int)(onTime * scale);

                // Stacked bar
                var bar = new VisualElement();
                bar.style.width = 24;
                bar.style.flexDirection = FlexDirection.Column;
                bar.style.borderTopLeftRadius = 4;
                bar.style.borderTopRightRadius = 4;
                bar.style.overflow = Overflow.Hidden;

                // Absent segment (yellow) - top
                if (absentH > 0)
                {
                    var seg = new VisualElement();
                    seg.style.height = absentH;
                    seg.style.width = Length.Percent(100);
                    seg.style.backgroundColor = new Color(0.99f, 0.8f, 0.43f); // #fdcb6e
                    seg.AddToClassList("bar-segment");
                    seg.AddToClassList("segment-absent");
                    seg.userData = new TooltipInfo { DateStr = dayDate.ToString("dd/MM"), CountStr = $"Vắng: {absent}" };
                    bar.Add(seg);
                }

                // Late segment (red)
                if (lateH > 0)
                {
                    var seg = new VisualElement();
                    seg.style.height = lateH;
                    seg.style.width = Length.Percent(100);
                    seg.style.backgroundColor = new Color(1f, 0.46f, 0.46f); // #ff7675
                    seg.AddToClassList("bar-segment");
                    seg.AddToClassList("segment-late");
                    seg.userData = new TooltipInfo { DateStr = dayDate.ToString("dd/MM"), CountStr = $"Trễ: {late}" };
                    bar.Add(seg);
                }

                // OnTime segment (green) - bottom
                if (onTimeH > 0)
                {
                    var seg = new VisualElement();
                    seg.style.height = onTimeH;
                    seg.style.width = Length.Percent(100);
                    seg.style.backgroundColor = new Color(0f, 0.72f, 0.58f); // #00B894
                    seg.AddToClassList("bar-segment");
                    seg.AddToClassList("segment-ontime");
                    seg.userData = new TooltipInfo { DateStr = dayDate.ToString("dd/MM"), CountStr = $"Đúng giờ: {onTime}" };
                    bar.Add(seg);
                }

                barArea.Add(bar);
            }

            col.Add(barArea);

            // Label
            var lbl = new Label(labels[i]);
            lbl.style.fontSize = 11;
            lbl.style.color = new Color(0.39f, 0.43f, 0.45f);
            lbl.style.marginTop = 4;
            lbl.style.unityTextAlign = TextAnchor.MiddleCenter;
            col.Add(lbl);

            chartContainer.Add(col);
        }

        SetupChartEvents();
    }

    private class TooltipInfo { public string DateStr; public string CountStr; }
    private VisualElement _chartTooltip;
    private Label _lblTooltipDate, _lblTooltipCount;

    void SetupChartEvents()
    {
        _chartTooltip = _uiRoot.Q<VisualElement>("ChartTooltip");
        _lblTooltipDate = _uiRoot.Q<Label>("TooltipDate");
        _lblTooltipCount = _uiRoot.Q<Label>("TooltipCount");

        // QUERY SEGMENTS INSTEAD OF BARS
        var segments = _uiRoot.Query<VisualElement>(className: "bar-segment").ToList();

        foreach (var seg in segments)
        {
            seg.RegisterCallback<MouseEnterEvent>(evt =>
            {
                if (_chartTooltip == null || seg.userData == null) return;

                var info = (TooltipInfo)seg.userData;
                if (_lblTooltipDate != null) _lblTooltipDate.text = info.DateStr;
                if (_lblTooltipCount != null) _lblTooltipCount.text = info.CountStr;

                _chartTooltip.style.display = DisplayStyle.Flex;

                // Position logic: Relative to Bar Group
                var bar = seg.parent; // .chart-bar
                if (bar != null)
                {
                    var group = bar.parent; // .chart-bar-group
                    if (group != null)
                    {
                        _chartTooltip.style.left = group.layout.x + (group.layout.width / 2) - 40;
                        _chartTooltip.style.bottom = group.layout.height + 10;
                        _chartTooltip.style.top = StyleKeyword.Auto;
                    }
                }
            });

            seg.RegisterCallback<MouseLeaveEvent>(evt =>
            {
                if (_chartTooltip != null) _chartTooltip.style.display = DisplayStyle.None;
            });
        }
    }

    // Tách hàm lọc để dùng chung
    List<AttendanceLog> FilterLogs(List<AttendanceLog> source)
    {
        var filtered = new List<AttendanceLog>(source);

        // 1. Lọc ngày
        if (_dateFilterMode != "All")
        {
            DateTime now = DateTime.Now;
            filtered = filtered.Where(x =>
            {
                DateTime logDate = new DateTime(x.timestamp);
                if (_dateFilterMode == "Today") return logDate.Date == now.Date;
                if (_dateFilterMode == "Yesterday") return logDate.Date == now.Date.AddDays(-1);
                return true;
            }).ToList();
        }

        // 2. Lọc Search
        if (!string.IsNullOrEmpty(_searchText))
        {
            filtered = filtered.Where(x =>
                x.studentName.ToLower().Contains(_searchText) ||
                x.mssv.Contains(_searchText)
            ).ToList();
        }

        return filtered;
    }

    // --- PHẦN 3: HIỂN THỊ NỘI DUNG ---

    // [NEW] Hàm Setup ListView cho UI Toolkit mới
    void SetupListView()
    {
        // 1. Tìm ListView trong giao diện
        _mainListView = _uiRoot.Q<ListView>("MainListView");
        if (_mainListView == null) return;

        // 2. Cấu hình nguồn dữ liệu
        _mainListView.itemsSource = _allLogs; // Liên kết trực tiếp với List dữ liệu cũ của bạn

        // 3. Hàm TẠO DÒNG (Make Item) - Clone từ mẫu UXML
        _mainListView.makeItem = () =>
        {
            if (logItemTemplate == null)
            {
                // Fallback nếu chưa có template
                var fallback = new VisualElement();
                fallback.Add(new Label("No template"));
                return fallback;
            }
            var newItem = logItemTemplate.Instantiate();
            return newItem;
        };

        // 4. Hàm ĐỔ DỮ LIỆU (Bind Item) - Điền chữ vào dòng
        _mainListView.bindItem = (element, index) =>
        {
            var log = _allLogs[index];

            // Tìm các label trong dòng (theo tên mới trong UXML)
            var lblName = element.Q<Label>("LblName");
            var lblMssv = element.Q<Label>("LblMssv"); // [NEW] Bind MSSV
            var lblTime = element.Q<Label>("LblTime");
            var lblDate = element.Q<Label>("LblDate"); // [NEW] Bind Date
            var lblStatus = element.Q<Label>("LblStatus");
            var lblAvatarChar = element.Q<Label>("LblAvatarChar");

            if (lblName != null) lblName.text = log.studentName;
            if (lblMssv != null) lblMssv.text = log.mssv; // [NEW] Bind MSSV
            if (lblTime != null) lblTime.text = log.time;
            if (lblDate != null) lblDate.text = log.date ?? "";

            // Set Avatar Character (First letter of name)
            if (lblAvatarChar != null && !string.IsNullOrEmpty(log.studentName))
            {
                lblAvatarChar.text = log.studentName.Substring(0, 1).ToUpper();
            }

            // Set Status Color/Text
            if (lblStatus != null)
            {
                lblStatus.text = (log.status == "Late") ? "Đi trễ" : "Đúng giờ";

                // Reset styling classes
                lblStatus.RemoveFromClassList("tag-status-active");
                lblStatus.RemoveFromClassList("tag-status-pending"); // Reused for red style if define new class

                // Custom color based on status
                if (log.status == "Late")
                {
                    lblStatus.style.backgroundColor = new Color(1f, 0.9f, 0.9f); // Red bg
                    lblStatus.style.color = new Color(0.9f, 0.3f, 0.3f); // Red text
                }
                else
                {
                    lblStatus.style.backgroundColor = new Color(0.9f, 0.95f, 1f); // Blue bg
                    lblStatus.style.color = new Color(0.04f, 0.52f, 0.89f); // Blue text
                }
            }
        };

    }

    // --- STUDENT LIST LOGIC (NEW) ---

    void SetupStudentListView()
    {
        _studentListView = _uiRoot.Q<ListView>("StudentListView");
        if (_studentListView == null) return;

        _studentListView.itemsSource = _studentDataList;
        _studentListView.makeItem = () =>
        {
            if (studentItemTemplate == null) return new Label("No template");
            return studentItemTemplate.Instantiate();
        };

        _studentListView.bindItem = (element, index) =>
        {
            if (index < 0 || index >= _studentDataList.Count) return;
            var stat = _studentDataList[index];

            var lblAvatar = element.Q<Label>("LblAvatarChar");
            var lblName = element.Q<Label>("LblName");
            var lblMssv = element.Q<Label>("LblMssv");
            var btnDetail = element.Q<Button>("BtnDetail");

            if (lblName != null) lblName.text = stat.Name;
            if (lblMssv != null) lblMssv.text = stat.MSSV;
            if (lblAvatar != null) lblAvatar.text = string.IsNullOrEmpty(stat.Name) ? "?" : stat.Name.Substring(0, 1).ToUpper();

            // [NEW] Check registration status from profiles cache
            bool isRegistered = _allProfilesCache?.Any(p =>
                p.mssv == stat.MSSV ||
                (p.name?.ToLower() == stat.Name?.ToLower())) ?? false;
            stat.IsRegistered = isRegistered;

            // [NEW] Show registration status label
            var lblStatus = element.Q<Label>("LblRegStatus");
            if (lblStatus == null)
            {
                // Create status label if not exists
                lblStatus = new Label();
                lblStatus.name = "LblRegStatus";
                lblStatus.style.fontSize = 11;
                lblStatus.style.paddingLeft = 8;
                lblStatus.style.paddingRight = 8;
                lblStatus.style.paddingTop = 3;
                lblStatus.style.paddingBottom = 3;
                lblStatus.style.borderTopLeftRadius = 10;
                lblStatus.style.borderTopRightRadius = 10;
                lblStatus.style.borderBottomLeftRadius = 10;
                lblStatus.style.borderBottomRightRadius = 10;
                lblStatus.style.marginLeft = 10;
                element.Add(lblStatus);
            }

            if (isRegistered)
            {
                lblStatus.text = "✅ Đã đăng ký";
                lblStatus.style.backgroundColor = ColorFromHex("#d4edda");
                lblStatus.style.color = ColorFromHex("#155724");
            }
            else
            {
                lblStatus.text = "⚠ Chưa đăng ký";
                lblStatus.style.backgroundColor = ColorFromHex("#fff3cd");
                lblStatus.style.color = ColorFromHex("#856404");
            }

            if (btnDetail != null)
            {
                // Lưu index vào userData để click handler biết lấy đúng sinh viên
                btnDetail.userData = index;

                // Chỉ đăng ký 1 lần (kiểm tra đã đăng ký chưa)
                if (btnDetail.GetClasses().Contains("event-bound") == false)
                {
                    btnDetail.AddToClassList("event-bound");
                    btnDetail.clicked += () =>
                    {
                        int idx = (int)(btnDetail.userData ?? 0);
                        if (idx >= 0 && idx < _studentDataList.Count)
                        {
                            ShowStudentDetail(_studentDataList[idx]);
                        }
                    };
                }
            }
        };
    }

    void FetchAndShowStudentList()
    {
        // 1. Tổng hợp danh sách sinh viên Duy Nhất từ Logs
        // (Sau này sẽ thay bằng gọi API /profiles)

        var dict = new Dictionary<string, StudentStat>();
        var dateCheckInCount = new Dictionary<string, int>(); // [NEW] Đếm số người điểm danh mỗi ngày
        var dateStudents = new Dictionary<string, HashSet<string>>(); // [NEW] Lưu unique students per day

        foreach (var log in _allLogs)
        {
            // Bỏ qua log rác
            if (string.IsNullOrEmpty(log.studentName) || log.studentName == "Unknown") continue;

            // Lấy ngày từ timestamp (Unix timestamp dạng long - có thể là seconds hoặc milliseconds)
            string logDate = "";
            if (log.timestamp > 0)
            {
                try
                {
                    long ts = log.timestamp;
                    // Nếu timestamp quá lớn (> 10 tỷ), có thể là milliseconds -> chia cho 1000
                    if (ts > 10000000000L)
                    {
                        ts = ts / 1000;
                    }
                    var dt = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
                    logDate = dt.ToString("yyyy-MM-dd");
                }
                catch
                {
                    // Bỏ qua nếu timestamp không hợp lệ
                    logDate = "";
                }
            }

            string key = log.studentName + "_" + log.mssv; // Key duy nhất

            if (!dict.ContainsKey(key))
            {
                dict[key] = new StudentStat
                {
                    Name = log.studentName,
                    MSSV = log.mssv,
                    TotalCheckIn = 0,
                    LateCount = 0,
                    OnTimeCount = 0,
                    AbsentCount = 0
                };
            }

            // Tính toán thống kê luôn
            dict[key].TotalCheckIn++;
            if (log.status == "Late") dict[key].LateCount++;
            else dict[key].OnTimeCount++;

            // Lưu ngày đã điểm danh cho sinh viên này
            if (!string.IsNullOrEmpty(logDate))
            {
                dict[key].AttendedDates.Add(logDate);

                // [NEW] Đếm unique students per day
                if (!dateStudents.ContainsKey(logDate))
                    dateStudents[logDate] = new HashSet<string>();
                dateStudents[logDate].Add(key);
            }
        }

        // [NEW] Xác định "ngày hợp lệ" = ngày có >= 1 người điểm danh (Modified for testing)
        const int MIN_CHECKIN_FOR_VALID_DAY = 1;
        var validDays = new HashSet<string>();
        foreach (var kvp in dateStudents)
        {
            if (kvp.Value.Count >= MIN_CHECKIN_FOR_VALID_DAY)
            {
                validDays.Add(kvp.Key);
            }
        }
        Debug.Log($"[FetchStudentList] Valid class days (>= {MIN_CHECKIN_FOR_VALID_DAY} students): {validDays.Count} days");

        // [NEW] Tính số buổi vắng cho mỗi sinh viên (chỉ tính ngày hợp lệ)
        int totalValidDays = validDays.Count;
        foreach (var stat in dict.Values)
        {
            // Đếm bao nhiêu ngày hợp lệ mà sinh viên có mặt
            int attendedValidDays = 0;
            foreach (var date in stat.AttendedDates)
            {
                if (validDays.Contains(date))
                    attendedValidDays++;
            }
            stat.AbsentCount = totalValidDays - attendedValidDays;
        }

        _studentDataList = dict.Values.ToList();

        // 2. Refresh List
        if (_studentListView != null)
        {
            _studentListView.itemsSource = _studentDataList;
            _studentListView.Rebuild();
        }
    }

    void ShowStudentDetail(StudentStat stat)
    {
        Debug.Log($"[ShowStudentDetail] Called for: {stat?.Name}");

        if (_modalContainer == null) { Debug.LogError("_modalContainer is null!"); return; }
        if (modalStudentDetailTemplate == null) { Debug.LogError("modalStudentDetailTemplate is null!"); return; }

        _modalContainer.Clear();
        var modal = modalStudentDetailTemplate.Instantiate();
        modal.style.width = Length.Percent(100);
        modal.style.height = Length.Percent(100);
        _modalContainer.Add(modal);
        _modalContainer.style.display = DisplayStyle.Flex;

        // [NEW] Modal Entry Animation (Scale Up)
        var modalCard = modal.Q<VisualElement>(className: "modal-card");
        if (modalCard != null)
        {
            modalCard.AddToClassList("modal-hidden");
            modalCard.schedule.Execute(() =>
            {
                modalCard.RemoveFromClassList("modal-hidden");
                modalCard.AddToClassList("modal-visible");
            }).StartingIn(20);
        }

        // QUAN TRỌNG: Hiện ModalOverlay (mặc định bị ẩn trong UXML)
        var overlay = modal.Q<VisualElement>("ModalOverlay");
        if (overlay != null) overlay.style.display = DisplayStyle.Flex;
        // Bind Data to Modal
        var lblName = modal.Q<Label>("ModalName");
        var lblMssv = modal.Q<Label>("ModalMssv");
        var charAvatar = modal.Q<Label>("ModalAvatarChar");

        var lblTotal = modal.Q<Label>("ModalTotal");
        // var lblOnTime = modal.Q<Label>("ModalOnTime"); // Layout mới có thể không hiện OnTime, hoặc add lại nếu cần
        var lblLate = modal.Q<Label>("ModalLate");

        var lblWarnLate = modal.Q<Label>("LblWarnLate");
        var lblWarnAbsent = modal.Q<Label>("LblWarnAbsent"); // Placeholder cho vắng

        var btnClose = modal.Q<Button>("BtnCloseModal");

        if (lblName != null) lblName.text = stat.Name;
        if (lblMssv != null) lblMssv.text = "MSSV: " + stat.MSSV;
        if (charAvatar != null)
        {
            charAvatar.text = string.IsNullOrEmpty(stat.Name) ? "?" : stat.Name.Substring(0, 1).ToUpper();
            charAvatar.style.display = DisplayStyle.Flex;
        }

        // Load Avatar
        var imgAvatar = modal.Q<VisualElement>("ModalAvatarImage");
        if (imgAvatar != null)
        {
            imgAvatar.style.display = DisplayStyle.None;
            // ✅ FIX #3: Use EnsureAvatarLoaded for reliable avatar loading with retry
            string prefix = null;
            if (_allProfilesCache != null)
            {
                var fp = _allProfilesCache.FirstOrDefault(x => (x.mssv ?? "").Trim() == (stat.MSSV ?? "").Trim());
                prefix = fp?.prefix;
            }
            StartCoroutine(EnsureAvatarLoaded(stat.Name, stat.MSSV, imgAvatar, charAvatar, prefix));
        }

        if (lblTotal != null) lblTotal.text = stat.TotalCheckIn.ToString();

        // Logic Cảnh Báo Trễ
        if (lblLate != null)
        {
            lblLate.text = stat.LateCount.ToString();
            // Nếu trễ > 3 -> Hiện cảnh báo
            if (stat.LateCount > 3 && lblWarnLate != null)
            {
                lblWarnLate.style.display = DisplayStyle.Flex;
                lblWarnLate.text = "⚠ Báo động (Trễ > 3)";
            }
            else if (lblWarnLate != null)
            {
                lblWarnLate.style.display = DisplayStyle.None;
            }
        }

        // [NEW] Logic Cảnh Báo Vắng - Phương án A: đếm theo ngày có học + đã kết thúc
        var lblAbsent = modal.Q<Label>("ModalAbsent");
        GetSelectedDateRange(out DateTime rangeStart, out DateTime rangeEnd);
        int absentInRange = CountAbsentInRange(stat.MSSV, rangeStart, rangeEnd);
        
        if (lblAbsent != null) lblAbsent.text = absentInRange.ToString();

        if (absentInRange > 3 && lblWarnAbsent != null)
        {
            lblWarnAbsent.style.display = DisplayStyle.Flex;
            lblWarnAbsent.text = "⚠ Cấm thi (Vắng > 3)";
        }
        else if (lblWarnAbsent != null)
        {
            lblWarnAbsent.style.display = DisplayStyle.None;
        }

        // [NEW] Breakdown theo từng lớp (hữu ích khi SV học nhiều lớp)
        RenderStudentMultiClassBreakdown(modal, stat, rangeStart, rangeEnd);

        // Logic đóng modal
        if (btnClose != null) btnClose.clicked += () => _modalContainer.style.display = DisplayStyle.None;

        // [NEW] Logic Xóa Sinh Viên
        var btnDelete = modal.Q<Button>("BtnDeleteStudent");
        if (btnDelete != null)
        {
            btnDelete.clicked += () =>
            {
                Debug.Log("==== DELETE BUTTON CLICKED ===="); // [DEBUG] Confirm click

                // [FIX] Server expects "Name|MSSV" format (with pipe separator)
                string profileKey = $"{stat.Name}|{stat.MSSV}";
                Debug.Log($"[DeleteStudent] Deleting profile: {profileKey}");

                // Disable button to prevent double click
                btnDelete.SetEnabled(false);
                btnDelete.text = "Đang xóa...";

                // Call API
                StartCoroutine(DeleteProfile(profileKey, (success) =>
                {
                    if (success)
                    {
                        Debug.Log($"[DeleteStudent] Successfully deleted: {profileKey}");
                        ShowToast("✅ Đã hủy đăng ký khuôn mặt!", new Color(0, 0.72f, 0.58f));

                        // ✅ FIX #7: Chỉ unregister, KHÔNG xóa khỏi danh sách lớp, KHÔNG xóa logs
                        stat.IsRegistered = false;

                        // Remove khỏi cache profiles để badge đổi ngay
                        if (_allProfilesCache != null)
                        {
                            _allProfilesCache.RemoveAll(p =>
                                p.profile_key == profileKey ||
                                (p.mssv ?? "").Trim() == (stat.MSSV ?? "").Trim() ||
                                (!string.IsNullOrEmpty(p.name) && !string.IsNullOrEmpty(stat.Name) &&
                                 p.name.Trim().Equals(stat.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                            );
                        }

                        // Đảm bảo item trong roster cũng về trạng thái chưa đăng ký
                        var rosterStat = _studentDataList?.FirstOrDefault(s => (s.MSSV ?? "").Trim() == (stat.MSSV ?? "").Trim());
                        if (rosterStat != null) rosterStat.IsRegistered = false;

                        // Close modal
                        _modalContainer.style.display = DisplayStyle.None;

                        // Refresh UI liên quan (để status/ảnh cập nhật)
                        FetchAndRenderStudentList();   // kéo profiles mới + rebuild list (scrollview)
                        RenderRegisterTabContent();    // sync tab đăng ký
                        InitDashboardStats();          // sync dashboard card

                        // Nếu còn dùng ListView cũ
                        if (_studentListView != null)
                        {
                            _studentListView.itemsSource = _studentDataList;
                            _studentListView.Rebuild();
                        }
                    }
                    else
                    {
                        Debug.LogError($"[DeleteStudent] Failed to delete: {profileKey}");
                        ShowToast("❌ Hủy đăng ký thất bại!", new Color(0.84f, 0.19f, 0.19f));
                        btnDelete.SetEnabled(true);
                        btnDelete.text = "Hủy đăng ký";
                    }
                }));
            };
        }
    }

    // [PHƯƠNG ÁN A] Khi sinh viên học nhiều lớp: hiển thị breakdown theo từng lớp/môn
    private void RenderStudentMultiClassBreakdown(VisualElement modal, StudentStat stat, DateTime start, DateTime end)
    {
        try
        {
            if (modal == null || stat == null) return;

            // Remove previous section (avoid duplicates)
            var old = modal.Q<VisualElement>("MultiClassBreakdown");
            if (old != null) old.RemoveFromHierarchy();

            var opts = GetClassOptionsFromSchedule();
            if (opts == null || opts.Count <= 1) return;

            var card = modal.Q<VisualElement>(className: "modal-card");
            if (card == null) return;

            var btnDelete = modal.Q<Button>("BtnDeleteStudent");
            int insertIndex = (btnDelete != null) ? card.IndexOf(btnDelete) : card.childCount;

            var section = new VisualElement();
            section.name = "MultiClassBreakdown";
            section.style.marginTop = 14;
            section.style.marginBottom = 8;

            var title = new Label("Theo từng lớp (môn)");
            title.style.fontSize = 14;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = new Color(0.2f, 0.2f, 0.2f);
            title.style.marginBottom = 6;
            section.Add(title);

            var sv = new ScrollView();
            sv.style.maxHeight = 170;
            sv.style.backgroundColor = new Color(0.98f, 0.98f, 0.99f);
            sv.style.borderTopLeftRadius = 10;
            sv.style.borderTopRightRadius = 10;
            sv.style.borderBottomLeftRadius = 10;
            sv.style.borderBottomRightRadius = 10;
            sv.style.paddingLeft = 10;
            sv.style.paddingRight = 10;
            sv.style.paddingTop = 8;
            sv.style.paddingBottom = 8;
            section.Add(sv);

            string termId = string.IsNullOrEmpty(_currentTermId) ? CurrentTermId : _currentTermId;
            string targetMssv = (stat.MSSV ?? "").Trim();
            DateTime today = DateTime.Today;

            foreach (var opt in opts)
            {
                string classCode = (opt.classCode ?? "").Trim();
                if (string.IsNullOrEmpty(classCode)) continue;

                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Column;
                row.style.paddingTop = 6;
                row.style.paddingBottom = 8;
                row.style.borderBottomWidth = 1;
                row.style.borderBottomColor = new Color(0.92f, 0.92f, 0.93f);

                var lbl = new Label();
                lbl.style.fontSize = 12;
                lbl.style.color = new Color(0.25f, 0.25f, 0.25f);
                lbl.style.whiteSpace = WhiteSpace.Normal;
                row.Add(lbl);

                var barBg = new VisualElement();
                barBg.style.height = 6;
                barBg.style.marginTop = 6;
                barBg.style.backgroundColor = new Color(0.9f, 0.9f, 0.92f);
                barBg.style.borderTopLeftRadius = 3;
                barBg.style.borderTopRightRadius = 3;
                barBg.style.borderBottomLeftRadius = 3;
                barBg.style.borderBottomRightRadius = 3;

                var barFill = new VisualElement();
                barFill.style.height = 6;
                barFill.style.width = Length.Percent(0);
                barFill.style.backgroundColor = new Color(0.0f, 0.72f, 0.58f);
                barFill.style.borderTopLeftRadius = 3;
                barFill.style.borderTopRightRadius = 3;
                barFill.style.borderBottomLeftRadius = 3;
                barFill.style.borderBottomRightRadius = 3;

                barBg.Add(barFill);
                row.Add(barBg);
                sv.Add(row);

                void ApplyStats(List<AttendanceLog> logs, bool hasLocal)
                {
                    logs ??= new List<AttendanceLog>();

                    // session days = ngày có học (đã kết thúc) + ngày phát sinh có log (make-up)
                    var sessionDays = new HashSet<string>();

                    for (var d = start.Date; d <= end.Date; d = d.AddDays(1))
                    {
                        if (d > today) continue;
                        if (HasClassFor(d, classCode) && HasClassFinishedFor(d, classCode))
                            sessionDays.Add(d.ToString("dd/MM/yyyy"));
                    }

                    foreach (var lg in logs)
                    {
                        if (string.IsNullOrEmpty(lg.date)) continue;
                        if (!DateTime.TryParseExact(lg.date, "dd/MM/yyyy", null,
                            System.Globalization.DateTimeStyles.None, out var dt)) continue;

                        dt = dt.Date;
                        if (dt < start.Date || dt > end.Date) continue;
                        if (dt >= today) continue;
                        sessionDays.Add(dt.ToString("dd/MM/yyyy"));
                    }

                    int totalSessions = sessionDays.Count;

                    int present = 0;
                    int late = 0;

                    var byDate = logs
                        .Where(l => (l.mssv ?? "").Trim() == targetMssv)
                        .GroupBy(l => l.date)
                        .ToDictionary(g => g.Key, g => g.ToList());

                    foreach (var dStr in sessionDays)
                    {
                        if (!byDate.TryGetValue(dStr, out var list)) continue;

                        if (list.All(x => IsAbsentStatusHelper(x.status)))
                            continue;

                        present++;

                        if (list.Any(x => (x.status ?? "").Equals("Late", StringComparison.OrdinalIgnoreCase) ||
                                          (x.status ?? "").Equals("Trễ", StringComparison.OrdinalIgnoreCase)))
                            late++;
                    }

                    int absent = Math.Max(0, totalSessions - present);
                    float rate = totalSessions <= 0 ? 0f : (present * 100f / totalSessions);
                    barFill.style.width = Length.Percent(rate);

                    string label = FormatClassLabel(opt);
                    string hint = hasLocal ? "" : " • (chưa sync log)";
                    lbl.text = $"{label}{hint} — Đi {present}/{totalSessions} • Vắng {absent} • Trễ {late}";
                }

                bool hasLocalFile = File.Exists(GetLogsLocalPathFor(termId, classCode));
                var localLogs = LoadLogsLocalFor(termId, classCode);
                ApplyStats(localLogs, hasLocalFile);

                string reqKey = $"{termId}__{classCode}";
                if (!hasLocalFile && !_multiClassLogSyncRequested.Contains(reqKey) && !string.IsNullOrEmpty(_currentTeacherId))
                {
                    _multiClassLogSyncRequested.Add(reqKey);
                    lbl.text += " • đang đồng bộ...";

                    StartCoroutine(FetchLogsFromServerFor(termId, classCode, fetched =>
                    {
                        ApplyStats(fetched, true);
                    }, silent: true));
                }
            }

            card.Insert(insertIndex, section);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MultiClass] Render breakdown failed: {e.Message}");
        }
    }


    // [NEW] Hiện popup kết quả điểm danh (On-time hoặc Late)
    void ShowAttendanceResult(string name, string mssv, string status)
    {
        Debug.Log($"[ShowAttendanceResult] {name} ({mssv}) - {status}");

        if (_modalContainer == null) { Debug.LogWarning("_modalContainer is null!"); return; }
        if (modalAttendanceResultTemplate == null) { Debug.LogWarning("modalAttendanceResultTemplate is null! Please assign in Inspector."); return; }

        _modalContainer.Clear();
        var modal = modalAttendanceResultTemplate.Instantiate();
        modal.style.width = Length.Percent(100);
        modal.style.height = Length.Percent(100);
        _modalContainer.Add(modal);
        _modalContainer.style.display = DisplayStyle.Flex;

        // Hiện ModalOverlay
        var overlay = modal.Q<VisualElement>("ModalOverlay");
        if (overlay != null) overlay.style.display = DisplayStyle.Flex;

        // Bind Data
        var lblName = modal.Q<Label>("ModalName");
        var lblMssv = modal.Q<Label>("ModalMssv");
        var charAvatar = modal.Q<Label>("ModalAvatarChar");
        var lblStatusIcon = modal.Q<Label>("ModalStatusIcon");
        var lblStatusText = modal.Q<Label>("ModalStatusText");
        var lblTime = modal.Q<Label>("ModalTime");
        var statusBadge = modal.Q<VisualElement>("StatusBadge");
        var btnClose = modal.Q<Button>("BtnCloseModal");

        if (lblName != null) lblName.text = name;
        if (lblMssv != null) lblMssv.text = "MSSV: " + mssv;
        if (charAvatar != null) charAvatar.text = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpper();
        if (lblTime != null) lblTime.text = DateTime.Now.ToString("HH:mm:ss");

        // Status Style
        bool isOnTime = status == "On-time" || status == "OnTime";
        if (lblStatusIcon != null) lblStatusIcon.text = isOnTime ? "✓" : "⚠";
        if (lblStatusText != null) lblStatusText.text = isOnTime ? "Đúng giờ" : "Đi trễ";

        if (statusBadge != null)
        {
            statusBadge.style.backgroundColor = isOnTime ? new Color(0.0f, 0.72f, 0.58f) : new Color(0.95f, 0.6f, 0.2f);
            // Apply padding/style
            statusBadge.style.paddingLeft = 12;
            statusBadge.style.paddingRight = 12;
            statusBadge.style.paddingTop = 6;
            statusBadge.style.paddingBottom = 6;
            statusBadge.style.borderTopLeftRadius = 8;
            statusBadge.style.borderTopRightRadius = 8;
            statusBadge.style.borderBottomLeftRadius = 8;
            statusBadge.style.borderBottomRightRadius = 8;
        }

        if (lblStatusIcon != null) lblStatusIcon.style.color = Color.white;
        if (lblStatusText != null) lblStatusText.style.color = Color.white;

        // Load Avatar
        var imgAvatar = modal.Q<VisualElement>("ModalAvatarImage");
        if (imgAvatar != null)
        {
            imgAvatar.style.display = DisplayStyle.None;
            // ✅ FIX #3: Use EnsureAvatarLoaded for reliable avatar loading with retry
            string prefix = null;
            if (_allProfilesCache != null)
            {
                var fp = _allProfilesCache.FirstOrDefault(x => (x.mssv ?? "").Trim() == (mssv ?? "").Trim());
                prefix = fp?.prefix;
            }
            StartCoroutine(EnsureAvatarLoaded(name, mssv, imgAvatar, charAvatar, prefix));
        }

        // Close button
        if (btnClose != null) btnClose.clicked += () => _modalContainer.style.display = DisplayStyle.None;

        // Auto-close after 4 seconds
        StartCoroutine(AutoCloseModal(4f));
    }

    IEnumerator AutoCloseModal(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (_modalContainer != null) _modalContainer.style.display = DisplayStyle.None;
    }
    [Serializable]
    public class FirebaseFileItem
    {
        public string name; // "photos/Name_MSSV/random.jpg"
        public string bucket;
    }
    [Serializable]
    public class FirebaseListResponse
    {
        public FirebaseFileItem[] items;
    }

    [Serializable]
    private class AvatarUrlResp
    {
        public string status;
        public string url;
        public string message;
    }

    // [NEW] Logic load ảnh thông minh hơn: List file trong folder -> Lấy file đầu tiên
    // [NEW] Logic load ảnh thông minh hơn: List file trong folder -> Lấy file đầu tiên

    // ✅ NEW: Helper giải quyết đường dẫn avatar đúng cách (multi-tenant)
    private string ResolveAvatarFolder(string name, string mssv, string serverPrefix = null)
    {
        // 1) Ưu tiên prefix server trả về (chuẩn nhất)
        string p = string.IsNullOrEmpty(serverPrefix) ? null : serverPrefix.Trim();

        // 2) Nếu không có prefix, tìm trong cache profiles theo MSSV
        if (string.IsNullOrEmpty(p) && _allProfilesCache != null)
        {
            var fp = _allProfilesCache.FirstOrDefault(x => x.mssv == mssv && x.name == name)
                  ?? _allProfilesCache.FirstOrDefault(x => x.mssv == mssv);

            if (fp != null && !string.IsNullOrEmpty(fp.prefix))
                p = fp.prefix.Trim();
        }

        // 3) Fallback: tự build theo teacherId (đúng cấu trúc bạn đang dùng trên server)
        if (string.IsNullOrEmpty(p))
        {
            if (!string.IsNullOrEmpty(_currentTeacherId))
                p = $"Teachers/{_currentTeacherId}/photos/{name}_{mssv}/";
            else
                p = $"photos/{name}_{mssv}/";
        }

        // LoadStudentAvatar tự thêm "/" nếu thiếu, nên mình trả về không cần "/" cuối cũng được
        return p.TrimEnd('/');
    }

    IEnumerator LoadStudentAvatar(string folderPath, VisualElement imgTarget, Label fallbackLabel, Action<bool> onDone = null)
    {
        bool success = false; // Track if avatar was loaded successfully

        // 1. List files in folder using Firebase Storage JSON API
        string bucket = "facecheckin-ai.firebasestorage.app";
        // Prefix phải encode "/" thành "%2F" nhưng trong query param 'prefix' thì giữ nguyên hoặc encode nhẹ
        // API: https://firebasestorage.googleapis.com/v0/b/[BUCKET]/o?prefix=[PREFIX]

        // --- STEP 1: LOAD VỚI PATH GỐC (CÓ SPACE) ---
        string prefix = folderPath.EndsWith("/") ? folderPath : folderPath + "/";
        string listUrl = $"https://firebasestorage.googleapis.com/v0/b/{bucket}/o?prefix={System.Uri.EscapeDataString(prefix)}&maxResults=1";

        Debug.Log($"[LoadAvatar] Listing files (Attempt 1): {listUrl}");

        using (UnityWebRequest wwwList = UnityWebRequest.Get(listUrl))
        {
            yield return wwwList.SendWebRequest();

            // Nếu thất bại hoặc không có items -> Thử STEP 2
            bool found = false;
            string firstFileName = "";

            if (wwwList.result == UnityWebRequest.Result.Success)
            {
                Debug.Log($"[LoadAvatar] RAW JSON: {wwwList.downloadHandler.text}"); // DEBUG
                var listData = JsonUtility.FromJson<FirebaseListResponse>(wwwList.downloadHandler.text);
                if (listData != null && listData.items != null && listData.items.Length > 0)
                {
                    firstFileName = listData.items[0].name;
                    found = true;
                }
            }
            else
            {
                Debug.LogWarning($"[LoadAvatar] Request failed: {wwwList.error}");
            }

            // --- STEP 2: LOAD VỚI PATH REPLACE SPACE -> UNDERSCORE ---
            if (!found && prefix.Contains(" ")) // Chỉ thử nếu tên có dấu cách
            {
                string prefixUnderscore = prefix.Replace(" ", "_");
                string listUrl2 = $"https://firebasestorage.googleapis.com/v0/b/{bucket}/o?prefix={System.Uri.EscapeDataString(prefixUnderscore)}&maxResults=1";
                Debug.Log($"[LoadAvatar] Empty. Trying Attempt 2 (Underscore): {listUrl2}");

                using (UnityWebRequest wwwList2 = UnityWebRequest.Get(listUrl2))
                {
                    yield return wwwList2.SendWebRequest();
                    if (wwwList2.result == UnityWebRequest.Result.Success)
                    {
                        var listData2 = JsonUtility.FromJson<FirebaseListResponse>(wwwList2.downloadHandler.text);
                        if (listData2 != null && listData2.items != null && listData2.items.Length > 0)
                        {
                            firstFileName = listData2.items[0].name;
                            found = true;
                        }
                    }
                }
            }

            // --- STEP 3: DOWN LOAD FILE NẾU TÌM THẤY ---
            if (found && !string.IsNullOrEmpty(firstFileName))
            {
                // Load ảnh đó
                string downloadUrl = $"https://firebasestorage.googleapis.com/v0/b/{bucket}/o/{System.Uri.EscapeDataString(firstFileName)}?alt=media";
                Debug.Log($"[LoadAvatar] Found file: {firstFileName}. Downloading...");

                using (UnityWebRequest wwwImg = UnityWebRequestTexture.GetTexture(downloadUrl))
                {
                    yield return wwwImg.SendWebRequest();
                    if (wwwImg.result == UnityWebRequest.Result.Success)
                    {
                        Texture2D tex = ((DownloadHandlerTexture)wwwImg.downloadHandler).texture;
                        if (tex != null)
                        {
                            success = true; // ✅ Avatar loaded successfully
                            imgTarget.style.backgroundImage = new StyleBackground(tex);
                            imgTarget.style.display = DisplayStyle.Flex;
                            if (fallbackLabel != null) fallbackLabel.style.display = DisplayStyle.None;
                        }
                    }
                }
            }
            else
            {
                Debug.LogWarning("[LoadAvatar] Folder empty or not found (Checked both Space and Underscore paths).");
            }
        }

        onDone?.Invoke(success); // ✅ Report result to caller
    }

    // ✅ FIX #3: Ensure avatar is loaded with retry mechanism (refresh profiles if needed)
    IEnumerator EnsureAvatarLoaded(string name, string mssv, VisualElement imgTarget, Label fallbackLabel, string serverPrefix = null)
    {
        string mm = (mssv ?? "").Trim();

        bool CacheHasPrefix()
        {
            if (!string.IsNullOrEmpty(serverPrefix)) return true;
            if (_allProfilesCache == null) return false;
            var fp = _allProfilesCache.FirstOrDefault(x => (x.mssv ?? "").Trim() == mm);
            return fp != null && !string.IsNullOrEmpty(fp.prefix);
        }

        bool ok = false;
        
        // ✅ Try signed URL first (bypasses Storage rules)
        bool okSigned = false;
        yield return StartCoroutine(LoadStudentAvatarViaSignedUrl(name, mssv, imgTarget, fallbackLabel, s => okSigned = s));
        if (okSigned) yield break;

        // Attempt 1: load with current cache/prefix
        string p1 = ResolveAvatarFolder(name, mssv, serverPrefix);
        yield return StartCoroutine(LoadStudentAvatar(p1, imgTarget, fallbackLabel, s => ok = s));
        if (ok) yield break;

        // Refresh profiles + Attempt 2
        if (!CacheHasPrefix())
        {
            Debug.Log("[EnsureAvatarLoaded] Avatar not found, refreshing profiles cache and retrying...");
            FaceProfile[] profiles = null;
            yield return StartCoroutine(GetProfiles(p => profiles = p));
            _allProfilesCache = profiles?.ToList() ?? new List<FaceProfile>();
        }

        bool ok2 = false;
        string p2 = ResolveAvatarFolder(name, mssv, null);
        yield return StartCoroutine(LoadStudentAvatar(p2, imgTarget, fallbackLabel, s => ok2 = s));
        if (ok2) yield break;

        // Legacy Signed URL fallback (if prioritized one failed, this counts as done)

    }

    // ✅ NEW: Load avatar via signed URL from Cloud Run API (bypasses Firebase Storage rules)
    private IEnumerator LoadStudentAvatarViaSignedUrl(string name, string mssv, VisualElement imgTarget, Label fallbackLabel, Action<bool> onDone = null)
    {
        bool ok = false;

        string api = cloudRunApiUrl.TrimEnd('/') + "/avatar_url"
            + "?mssv=" + UnityWebRequest.EscapeURL((mssv ?? "").Trim())
            + "&name=" + UnityWebRequest.EscapeURL((name ?? "").Trim())
            + "&t=" + DateTime.Now.Ticks;

        using (var req = UnityWebRequest.Get(api))
        {
            ApplyTenantHeaders(req);
            yield return req.SendWebRequest();

            Debug.Log($"[AvatarSignedUrl] code={req.responseCode} result={req.result} body={req.downloadHandler.text} err={req.error}");

            if (req.result == UnityWebRequest.Result.Success)
            {
                var resp = JsonUtility.FromJson<AvatarUrlResp>(req.downloadHandler.text);
                if (resp != null && resp.status == "success" && !string.IsNullOrEmpty(resp.url))
                {
                    using (var imgReq = UnityWebRequestTexture.GetTexture(resp.url))
                    {
                        yield return imgReq.SendWebRequest();
                        if (imgReq.result == UnityWebRequest.Result.Success)
                        {
                            var tex = ((DownloadHandlerTexture)imgReq.downloadHandler).texture;
                            if (tex != null)
                            {
                                ok = true;
                                imgTarget.style.backgroundImage = new StyleBackground(tex);
                                imgTarget.style.display = DisplayStyle.Flex;
                                if (fallbackLabel != null) fallbackLabel.style.display = DisplayStyle.None;
                            }
                        }
                    }
                }
            }
        }

        onDone?.Invoke(ok);
    }

    // [NEW] Hàm Refresh dùng cho ListView
    void RefreshMainContent()
    {
        // Với ListView, phải cập nhật nguồn dữ liệu và rebuild
        if (_mainListView != null)
        {
            // [FIX] Filter logs before displaying (Apply Search & Date filters)
            var filtered = FilterLogs(_allLogs);

            // [FIX] Sort by timestamp descending (newest first)
            filtered = filtered.OrderByDescending(l => l.timestamp).ToList();

            _mainListView.itemsSource = filtered; // Cập nhật lại source
            _mainListView.Rebuild(); // Vẽ lại UI
            Debug.Log($"[DEBUG] RefreshMainContent: itemsSource updated, count = {filtered.Count}");
        }
    }


    VisualElement CreateStatItemRow(StudentStat stat)
    {
        var row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        row.style.backgroundColor = Color.white;
        row.style.borderBottomWidth = 1;
        row.style.borderBottomColor = new Color(0.95f, 0.95f, 0.98f);
        row.style.height = 60;
        row.style.alignItems = Align.Center;
        row.style.paddingLeft = 25; row.style.paddingRight = 25;

        var lblName = new Label(stat.Name);
        lblName.style.width = Length.Percent(35);
        lblName.style.unityFontStyleAndWeight = FontStyle.Bold;
        lblName.style.color = colTextDark;

        var lblMssv = new Label(stat.MSSV);
        lblMssv.style.width = Length.Percent(20);
        lblMssv.style.color = colTextLight;

        var lblOnTime = new Label($"{stat.OnTimeCount}");
        lblOnTime.style.width = Length.Percent(15);
        lblOnTime.style.color = colGreen;
        lblOnTime.style.unityFontStyleAndWeight = FontStyle.Bold;

        var lblLate = new Label($"{stat.LateCount}");
        lblLate.style.width = Length.Percent(15);
        lblLate.style.color = colRed;
        lblLate.style.unityFontStyleAndWeight = FontStyle.Bold;

        if (stat.LateCount > 2) lblLate.text += " (!)";

        row.Add(lblName);
        row.Add(lblMssv);
        row.Add(lblOnTime);
        row.Add(lblLate);

        return row;
    }

    // --- NEW LOG CARD (Floating Style + Delete Button) ---
    VisualElement CreateLogItemCard(AttendanceLog log, int index = 0)
    {
        var card = new VisualElement();
        card.style.flexDirection = FlexDirection.Row;
        card.style.backgroundColor = Color.white;
        card.style.marginBottom = 15;
        card.style.paddingTop = 15; card.style.paddingBottom = 15;
        card.style.paddingLeft = 20; card.style.paddingRight = 20;
        card.style.borderTopLeftRadius = 12; card.style.borderTopRightRadius = 12;
        card.style.borderBottomLeftRadius = 12; card.style.borderBottomRightRadius = 12;

        // Shadow Fake (Subtle)
        card.style.borderBottomWidth = 2;
        card.style.borderBottomColor = new Color(0.9f, 0.9f, 0.92f);
        card.style.borderRightWidth = 1;
        card.style.borderRightColor = new Color(0.9f, 0.9f, 0.92f);

        // --- ANIMATION 1: STAGGERED ENTRY (XUẤT HIỆN SO LE) ---
        card.style.opacity = 0;
        // Fix: Translate requires Length objects, not ints
        card.style.translate = new Translate(new Length(0, LengthUnit.Pixel), new Length(30, LengthUnit.Pixel), 0);

        card.style.transitionProperty = new List<StylePropertyName> {
            new StylePropertyName("opacity"),
            new StylePropertyName("translate"),
            new StylePropertyName("scale"), // For hover
            new StylePropertyName("border-bottom-color") // For hover
        };
        card.style.transitionDuration = new List<TimeValue> { new TimeValue(0.4f) };
        card.style.transitionTimingFunction = new List<EasingFunction> { EasingMode.EaseOutCubic };

        // Delay based on index
        long delayMs = Math.Min(index * 40, 500); // Max delay 500ms to avoid waiting too long
        card.schedule.Execute(() =>
        {
            card.style.opacity = 1;
            card.style.translate = new Translate(new Length(0, LengthUnit.Pixel), new Length(0, LengthUnit.Pixel), 0);
        }).StartingIn(delayMs);

        // --- ANIMATION 4: HOVER 3D (TƯƠNG TÁC) ---
        card.RegisterCallback<MouseEnterEvent>(evt =>
        {
            card.style.scale = new Scale(new Vector3(1.02f, 1.02f, 1)); // Zoom in slightly
            card.style.borderBottomColor = colAccent; // Highlight shadow
        });
        card.RegisterCallback<MouseLeaveEvent>(evt =>
        {
            card.style.scale = new Scale(Vector3.one);
            card.style.borderBottomColor = new Color(0.9f, 0.9f, 0.92f); // Reset
        });

        // 1. Dynamic Avatar
        var avatar = new VisualElement();
        avatar.style.width = 42; avatar.style.height = 42;
        avatar.style.justifyContent = Justify.Center;
        avatar.style.alignItems = Align.Center;
        avatar.style.marginRight = 18;
        avatar.style.borderTopLeftRadius = 21; avatar.style.borderTopRightRadius = 21;
        avatar.style.borderBottomLeftRadius = 21; avatar.style.borderBottomRightRadius = 21;

        // Generate Color from Name
        Color avatarBg, avatarTxt;
        if (log.status == "Absent")
        {
            avatarBg = new Color(0.9f, 0.9f, 0.9f); // Grey for absent
            avatarTxt = Color.gray;
        }
        else
        {
            // Use safe name for hash
            string safeName = string.IsNullOrEmpty(log.studentName) ? "Unknown" : log.studentName;
            int hash = safeName.GetHashCode();
            float hue = Mathf.Abs(hash % 360) / 360f;
            avatarBg = Color.HSVToRGB(hue, 0.4f, 0.95f);
            avatarTxt = Color.HSVToRGB(hue, 0.8f, 0.4f);
        }

        avatar.style.backgroundColor = avatarBg;

        // Safety check for Substring
        string firstChar = "?";
        if (!string.IsNullOrEmpty(log.studentName) && log.studentName.Length > 0)
        {
            firstChar = log.studentName.Substring(0, 1).ToUpper();
        }

        avatar.Add(new Label(firstChar)
        {
            style = { color = avatarTxt, unityFontStyleAndWeight = FontStyle.Bold, fontSize = 16 }
        });

        // 2. Info Area
        var infoBox = new VisualElement();
        infoBox.style.flexGrow = 1;
        infoBox.Add(new Label(log.studentName) { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 15, color = colTextDark } });
        infoBox.Add(new Label(log.mssv) { style = { fontSize = 12, color = colTextLight, marginTop = 2 } });

        // 3. Status Pill
        string statusText = "";
        Color pillBg = Color.white;
        Color pillTxt = Color.black;

        if (log.status == "Late") { statusText = "Trễ"; pillBg = new Color(1f, 0.9f, 0.9f); pillTxt = colRed; }
        else if (log.status == "OnTime") { statusText = "Đúng giờ"; pillBg = new Color(0.9f, 1f, 0.95f); pillTxt = colGreen; }
        else if (log.status == "Absent") { statusText = "Vắng"; pillBg = new Color(1f, 0.95f, 0.8f); pillTxt = colOrange; }

        var pill = new Label(statusText);
        pill.style.backgroundColor = pillBg;
        pill.style.color = pillTxt;
        pill.style.paddingLeft = 12; pill.style.paddingRight = 12;
        pill.style.paddingTop = 5; pill.style.paddingBottom = 5;
        pill.style.fontSize = 12;
        pill.style.borderTopLeftRadius = 8; pill.style.borderTopRightRadius = 8;
        pill.style.borderBottomLeftRadius = 8; pill.style.borderBottomRightRadius = 8;
        pill.style.unityFontStyleAndWeight = FontStyle.Bold;

        // --- ANIMATION 3: PULSE EFFECT (CHECK-IN MỚI) ---
        string relativeTime = GetRelativeTime(log.time);
        if (relativeTime == "Vừa xong" && log.status != "Absent")
        {
            // Simple opacity pulse using schedule
            pill.schedule.Execute(() =>
            {
                pill.style.borderTopWidth = 1; pill.style.borderBottomWidth = 1;
                pill.style.borderLeftWidth = 1; pill.style.borderRightWidth = 1;
                pill.style.borderTopColor = colAccent; pill.style.borderBottomColor = colAccent;
                pill.style.borderLeftColor = colAccent; pill.style.borderRightColor = colAccent;
            });
        }

        // 4. Time
        var timeLabel = new Label(relativeTime);
        timeLabel.style.marginLeft = 20;
        timeLabel.style.width = 100; // Fixed width to align delete button
        timeLabel.style.unityTextAlign = TextAnchor.MiddleRight;
        timeLabel.style.color = colTextLight;
        timeLabel.style.fontSize = 12;

        card.Add(avatar);
        card.Add(infoBox);
        card.Add(pill);
        card.Add(timeLabel);

        // 5. Delete Button (X) - Only for real logs
        if (log.status != "Absent")
        {
            var btnDelete = new Button();
            btnDelete.text = "✕";
            btnDelete.style.width = 30; btnDelete.style.height = 30;
            btnDelete.style.marginLeft = 15;
            btnDelete.style.backgroundColor = Color.clear;
            btnDelete.style.color = new Color(0.7f, 0.7f, 0.7f); // Grey default
            btnDelete.style.fontSize = 14;
            btnDelete.style.borderTopWidth = 0; btnDelete.style.borderBottomWidth = 0;
            btnDelete.style.borderLeftWidth = 0; btnDelete.style.borderRightWidth = 0;
            btnDelete.style.borderTopLeftRadius = 5; btnDelete.style.borderTopRightRadius = 5;
            btnDelete.style.borderBottomLeftRadius = 5; btnDelete.style.borderBottomRightRadius = 5;

            // Hover Effect
            btnDelete.RegisterCallback<MouseEnterEvent>(e =>
            {
                btnDelete.style.backgroundColor = new Color(1, 0.9f, 0.9f);
                btnDelete.style.color = colRed;
            });
            btnDelete.RegisterCallback<MouseLeaveEvent>(e =>
            {
                btnDelete.style.backgroundColor = Color.clear;
                btnDelete.style.color = new Color(0.7f, 0.7f, 0.7f);
            });

            // Click Event --- ANIMATION 2: DELETE ANIMATION ---
            btnDelete.clicked += () =>
            {
                // 1. Disable button
                btnDelete.SetEnabled(false);

                // 2. Animate Slide Out
                // Fix: Translate requires Length objects
                card.style.translate = new Translate(new Length(500, LengthUnit.Pixel), new Length(0, LengthUnit.Pixel), 0);
                card.style.opacity = 0; // Fade out

                // 3. Wait for animation then delete
                // 3. Wait for animation then delete
                card.schedule.Execute(() =>
                {
                    HideLogLocal(log.id); // [NEW] Chỉ ẩn Local
                    ShowToast("🗑 Đã xóa nhật ký (local)", colRed);
                }).StartingIn(350);
            };
            card.Add(btnDelete);
        }
        else
        {
            // Spacer for alignment
            var spacer = new VisualElement();
            spacer.style.width = 45;
            card.Add(spacer);
        }

        return card;
    }

    // [NEW] Helper Change Tab Style - Clears ALL tabs first
    void SwitchTabUI(Button activeBtn, params Button[] otherBtns)
    {
        // Lấy tất cả các nút trong sidebar và xóa class active
        var allTabs = _uiRoot.Query<Button>(className: "menu-item").ToList();
        foreach (var tab in allTabs)
        {
            tab.RemoveFromClassList("active");
        }

        // Thêm active cho nút được click
        if (activeBtn != null)
        {
            activeBtn.AddToClassList("active");
        }
    }

    string GetRelativeTime(string timeStr)
    {
        if (timeStr == "--:--") return ""; // Absent case

        if (DateTime.TryParse(timeStr, out DateTime eventTime))
        {
            TimeSpan span = DateTime.Now - eventTime;
            if (span.TotalSeconds < 0) return timeStr;
            if (span.TotalSeconds < 60) return "Vừa xong";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} phút trước";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours} giờ trước";
            return eventTime.ToString("dd/MM/yyyy");
        }
        return timeStr;
    }

    void ExportToCSV()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Ten Sinh Vien, MSSV, Thoi Gian, Trang Thai, Do Tin Cay (%)");

            foreach (var log in _allLogs)
            {
                string cleanName = log.studentName.Replace(",", "");
                string cleanMssv = log.mssv.Replace(",", "");
                sb.AppendLine($"{cleanName}, {cleanMssv}, {log.time}, {log.status}, {log.confidence}");
            }

            string desktopPath = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop);
            string fileName = $"BaoCao_DiemDanh_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            string finalPath = Path.Combine(desktopPath, fileName);
            File.WriteAllText(finalPath, sb.ToString(), Encoding.UTF8);
            Debug.Log($"Đã xuất file thành công: {finalPath}");

            if (_searchInput != null) _searchInput.value = "Đã xuất file ra Desktop!";
        }
        catch (Exception e)
        {
            Debug.LogError($"Lỗi xuất file: {e.Message}");
        }
    }

    private void ClearHiddenLogs()
    {
        _hiddenLogIds.Clear();
        try
        {
            if (File.Exists(HiddenLogLocalPath)) File.Delete(HiddenLogLocalPath);
        }
        catch (Exception e)
        {
            Debug.LogWarning("Cannot delete hidden_log_ids.json: " + e.Message);
        }

        ShowToast("✅ Đã khôi phục nhật ký đã ẩn", colGreen);
        StartCoroutine(FetchLogsFromServer(false)); // gọi lại để kéo log Firestore lên
    }

    // HÀM GỐC ĐÃ BỊ THAY THẾ!
    // Chúng ta không chạy file .exe nữa, mà chỉ cần BẬT Camera của Unity
    // và gửi ảnh qua API Cloud Run.
    private void TogglePythonScript()
    {
        bool newState = !_isCameraRunning;
        SetCameraDriver(newState);

        if (newState)
        {
            ShowToast("📷 Đã bật Nhận Diện AI", colGreen);

            // SHOW Camera Panel khi bật
            if (_uiCameraPreview != null) _uiCameraPreview.style.display = DisplayStyle.Flex;

            // Button Visual (UI Toolkit)
            if (_uiBtnCamera != null)
            {
                _uiBtnCamera.text = "STOP CAMERA";
                _uiBtnCamera.AddToClassList("btn-camera-off"); // Thêm class đỏ
            }

            // HIDE Main Content (List/Stats)
            if (_listContainer != null) _listContainer.style.display = DisplayStyle.None;
            if (_statsHeaderRow != null) _statsHeaderRow.style.display = DisplayStyle.None;

            // Start Recognition Loop (Mode: Attendance Check)
            _recognitionCoroutine = StartCoroutine(RecognitionLoop());
        }
        else
        {
            Debug.Log("⏹ AI loop OFF");
            ShowToast("⏹ Đã tắt Nhận Diện AI", colRed);

            // HIDE Camera Panel khi tắt
            if (_uiCameraPreview != null) _uiCameraPreview.style.display = DisplayStyle.None;

            // Button Visual (UI Toolkit)
            if (_uiBtnCamera != null)
            {
                _uiBtnCamera.text = "START CAMERA";
                _uiBtnCamera.RemoveFromClassList("btn-camera-off"); // Xóa class đỏ
            }

            // RESTORE Main Content
            RefreshMainContent(); // Restore view state

            if (_recognitionCoroutine != null)
            {
                StopCoroutine(_recognitionCoroutine);
                _recognitionCoroutine = null;
            }
        }
    }

    // [NEW] Core Camera Driver (No UI Toast, No Recognition Loop)
    // Dùng cho cả nút Toggle lẫn tính năng Add Face
    private void SetCameraDriver(bool active)
    {
        _isCameraRunning = active;

        if (active)
        {
            // 1. Auto-Find & Enable MediaPipe
            if (faceDetector == null)
            {
                var found = Resources.FindObjectsOfTypeAll<FaceDetectorRunner>();
                if (found != null && found.Length > 0)
                {
                    foreach (var f in found) { if (f.gameObject.scene.rootCount != 0) { faceDetector = f; break; } }
                }
            }

            if (faceDetector != null)
            {
                faceDetector.gameObject.SetActive(true);
                // [FIX] Explicitly call Play() to restart camera coroutine on subsequent opens
                faceDetector.Play();
            }
            else ShowToast("❌ Không tìm thấy FaceDetectorRunner!", colRed);

            // 2. SHOW Camera Preview in CENTER
            if (_cameraMainContainer != null)
            {
                _cameraMainContainer.style.display = DisplayStyle.Flex;

                // [FIX] Pause any existing scheduler before creating new one
                _cameraMainJob?.Pause();

                // Start Texture Update Loop
                _cameraMainJob = _cameraMainContainer.schedule.Execute(() =>
                {
                    if (!_isCameraRunning || _cameraMainContainer.style.display == DisplayStyle.None)
                    {
                        return;
                    }

                    // [FIX] Efficient full-frame preview
                    Texture2D newTex = CaptureFullFrame();
                    if (newTex != null)
                    {
                         // Since we are creating a new Texture2D every frame, we MUST destroy the old one
                         if (_capTex != null) Destroy(_capTex);
                         _capTex = newTex;
                         
                         if (_mirrorContainer != null) _mirrorContainer.style.backgroundImage = new StyleBackground(_capTex);
                         else _cameraMainContainer.style.backgroundImage = new StyleBackground(_capTex);
                    }

                    // --- P1: REAL-TIME FACE TRACKING & UI ---
                    if (faceDetector == null)
                    {
                        var found = Resources.FindObjectsOfTypeAll<FaceDetectorRunner>();
                        if (found != null && found.Length > 0)
                        {
                            foreach (var f in found) { if (f.gameObject.scene.rootCount != 0) { faceDetector = f; break; } }
                        }
                    }

                    if (faceDetector == null && _faceInfoLabel != null)
                    {
                        _faceTrackingBox.style.display = DisplayStyle.Flex;
                        _faceInfoLabel.text = "ERR: Missing FaceDetectorRunner!";
                        _faceInfoLabel.style.color = Color.magenta;
                    }
                    else if (TryGetFaceDataFromLastResult(out Rect r, out float yaw, out bool hasFace))
                    {
                        if (_faceTrackingBox != null)
                        {
                            _faceTrackingBox.style.display = DisplayStyle.Flex;
                            _faceTrackingBox.style.left = r.x * 640;
                            _faceTrackingBox.style.top = (1.0f - r.y - r.height) * 480;
                            _faceTrackingBox.style.width = r.width * 640;
                            _faceTrackingBox.style.height = r.height * 480;

                            if (_faceInfoLabel != null)
                            {
                                Color statusColor = Color.red;
                                string statusTxt = "Unknown";
                                float conf = 0f;

                                if (_lastVerifyResult != null)
                                {
                                    bool statusOk = (_lastVerifyResult.status == "success" || _lastVerifyResult.status == "recognized");
                                    if (statusOk)
                                    {
                                        statusTxt = _lastVerifyResult.name;
                                        statusColor = colGreen;
                                    }

                                    conf = _lastVerifyResult.match_percent;
                                }
                                else
                                {
                                    // No result yet -> Scanning
                                    statusTxt = "Scanning...";
                                    statusColor = Color.red; // Native Red
                                                             // If you want Text to be Yellow for scanning:
                                    _faceInfoLabel.style.color = Color.yellow;
                                }

                                // Update Text
                                if (statusTxt == "Scanning...")
                                    _faceInfoLabel.text = statusTxt;
                                else
                                    _faceInfoLabel.text = $"{statusTxt}\n{conf:F1}%";

                                // Update Colors
                                if (statusTxt != "Scanning...") _faceInfoLabel.style.color = statusColor;

                                _faceTrackingBox.style.borderTopColor = statusColor;
                                _faceTrackingBox.style.borderBottomColor = statusColor;
                                _faceTrackingBox.style.borderLeftColor = statusColor;
                                _faceTrackingBox.style.borderRightColor = statusColor;
                            }
                        }
                    }
                    else
                    {
                        if (_faceTrackingBox != null) _faceTrackingBox.style.display = DisplayStyle.None;
                    }
                    // --- END P1 ---

                }).Every(150); // ~6.5fps (reduced from 66ms for performance)
            }

            // --- [THÊM] HIỂN THỊ LÊN UI TOOLKIT MỚI (_uiCameraPreview) ---
            if (_uiCameraPreview != null)
            {
                // [FIX] Pause any existing job before creating new one
                _uiPreviewJob?.Pause();

                // Chạy vòng lặp update texture (10 FPS - giảm từ 30fps cho nhẹ)
                _uiPreviewJob = _uiCameraPreview.schedule.Execute(() =>
                {
                    if (!_isCameraRunning || _uiCameraPreview == null)
                    {
                        if (_uiPreviewLastTex != null) { Destroy(_uiPreviewLastTex); _uiPreviewLastTex = null; }
                        return;
                    }

                    Texture2D newTex = CaptureFromMediaPipe();
                    if (newTex == null) return;

                    var oldTex = _uiPreviewLastTex;
                    _uiPreviewLastTex = newTex;

                    _uiCameraPreview.style.backgroundImage = new StyleBackground(_uiPreviewLastTex);

                    if (oldTex != null) Destroy(oldTex);
                }).Every(200); // ✅ 200ms = ~5fps (reduced for performance)
            }
            // -----------------------------------------------

            // REMOVED: _recognitionCoroutine = StartCoroutine(RecognitionLoop());
        }
        else
        {
            // [FIX] Pause scheduled jobs
            _uiPreviewJob?.Pause();
            _uiPreviewJob = null;
            _cameraMainJob?.Pause();
            _cameraMainJob = null;

            // Tắt camera - UI TOOLKIT MỚI
            if (_uiCameraPreview != null) _uiCameraPreview.style.backgroundImage = null;
            // [FIX] Cleanup UI Preview Texture
            if (_uiPreviewLastTex != null) { Destroy(_uiPreviewLastTex); _uiPreviewLastTex = null; }

            // HIDE Camera Preview (UI cũ)
            if (_cameraMainContainer != null)
            {
                _cameraMainContainer.style.display = DisplayStyle.None;
                _cameraMainContainer.style.backgroundImage = null;
            }

            // [FIX] Disable FaceDetector to prevent GPU resource leak
            // ✅ FIX: Stop MediaPipe pipeline first to release camera (LED off)
            if (faceDetector != null)
            {
                faceDetector.Stop(); // Release camera driver
                faceDetector.gameObject.SetActive(false);
            }

            // Logic Stop Coroutine is handled in TogglePythonScript or OnDisable
        }

    }
    // [NEW] Helper to Stop Camera completely
    private void StopCameraRecognition()
    {
        Debug.Log("🛑 Stopping Camera Recognition (Match Found/User Request).");
        if (_isCameraRunning) TogglePythonScript(); // Re-use toggle off logic
    }

    // [TRACKING] Variable to lock identity
    private string _trackingName = "";
    private Material _cropMat;

    private Texture2D CaptureFromMediaPipe(int cropSize = 160)
    {
        _lastAlignMs = 0f;
        // Lấy nguồn ảnh
        var imageSource = ImageSourceProvider.ImageSource;
        if (imageSource == null) return null;

        Texture srcTex = imageSource.GetCurrentTexture();
        if (srcTex == null) return null;

        double t0 = Time.realtimeSinceStartupAsDouble;

        // --- KỸ THUẬT AN TOÀN: DÙNG RENDER TEXTURE ---
        // Tạo texture tạm để GPU copy dữ liệu sang
        RenderTexture tempRT = RenderTexture.GetTemporary(srcTex.width, srcTex.height, 0, RenderTextureFormat.ARGB32);

        // Copy ảnh từ Camera (GPU) sang tempRT
        Graphics.Blit(srcTex, tempRT);

        // Kích hoạt tempRT để chuẩn bị đọc
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = tempRT;

        // Tính toán tọa độ cắt (Lưu ý lật trục Y vì Unity toạ độ ngược)
        Rect r = _lastFaceRectNorm;
        int w = (int)(r.width * srcTex.width);
        int h = (int)(r.height * srcTex.height);
        int x = (int)(r.x * srcTex.width);
        int y = (int)((1.0f - r.y - r.height) * srcTex.height);

        // Kẹp giá trị (Clamp) để không bị crash nếu tính ra số âm
        x = Mathf.Clamp(x, 0, srcTex.width - 1);
        y = Mathf.Clamp(y, 0, srcTex.height - 1);
        w = Mathf.Clamp(w, 1, srcTex.width - x);
        h = Mathf.Clamp(h, 1, srcTex.height - y);

        // Đọc điểm ảnh từ GPU về CPU
        Texture2D croppedTex = new Texture2D(w, h, TextureFormat.RGB24, false);
        croppedTex.ReadPixels(new Rect(x, y, w, h), 0, 0);
        croppedTex.Apply();

        // Dọn dẹp
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(tempRT);

        _lastAlignMs = (float)((Time.realtimeSinceStartupAsDouble - t0) * 1000.0);
        return croppedTex;
    }

    // [NEW] Enqueue Log immediately
    private void EnqueueLogFromVerify(RecognitionResponse r)
    {
        string t = string.IsNullOrEmpty(r.log_time) ? DateTime.Now.ToString("HH:mm:ss") : r.log_time;
        var log = new AttendanceLog
        {
            id = string.IsNullOrEmpty(r.log_id) ? $"{DateTime.Now:yyyy-MM-dd}_{r.mssv}" : r.log_id,
            studentName = r.name,
            mssv = r.mssv,
            time = t,
            status = ComputeStatusByTime(t, "Present"),
            confidence = r.match_percent,
            timestamp = r.log_vn_epoch_ms > 0 ? EpochMsToTicks(r.log_vn_epoch_ms) : DateTime.Now.Ticks
        };
        lock (_lock) { _incomingQueue.Enqueue(log); }
    }

    // [NEW] Show Success and Exit
    // [NEW] Show Success and Exit
    private IEnumerator ShowSuccessThenExit(RecognitionResponse r)
    {
        // 1. Calculate status (OnTime/Late) based on current time
        string nowStr = DateTime.Now.ToString("HH:mm:ss");
        string status = ComputeStatusByTime(nowStr, "Present");

        // 2. Show Result Popup
        ShowAttendanceResult(r.name, r.mssv, status);

        // 3. Show Toast (backup)
        ShowToast($"✅ {r.name} ({r.mssv})", colGreen);

        yield return new WaitForSeconds(1.5f);

        if (_isCameraRunning) TogglePythonScript(); // Turn off camera
        yield return new WaitForSeconds(0.1f);

        StartCoroutine(FetchLogsFromServer(true)); // Pull new log from Firestore
    }

    // [REFACTORED] Recognition Loop (Fire & Forget + TRACKING)
    IEnumerator RecognitionLoop()
    {
        _verifyInFlight = false;
        _consecutiveHit = 0;
        _trackingName = ""; // Reset on start

        Debug.Log("🚀 Bắt đầu vòng lặp nhận diện..."); // [DEBUG]
        yield return new WaitForSeconds(1.0f);

        while (_isCameraRunning)
        {
            // Debug Loop State
            if (_verifyInFlight) Debug.Log("⏳ Đang đợi request cũ xong...");

            if (!_isReloading && !_verifyInFlight)
            {
                // [NEW] Cooldown check (B)
                if (Time.realtimeSinceStartup < _nextVerifyAllowedTime)
                {
                    yield return new WaitForSeconds(0.1f);
                    continue;
                }

                // [TRACKING LOGIC]
                // 1. Check Face
                if (TryGetFaceDataFromLastResult(out _, out _, out bool hasFace))
                {
                    if (!hasFace)
                    {
                        // Reset Tracking immediately if lost face
                        // Only reset if we were tracking? 
                        if (!string.IsNullOrEmpty(_trackingName))
                        {
                            _trackingName = "";
                            // Update UI to Scanning
                            if (_camLabel != null)
                            {
                                _camLabel.text = "🔍 Đang quét...";
                                _camLabel.style.color = Color.yellow;
                            }
                        }
                        _consecutiveHit = 0;
                        _lastVerifyResult = null;
                        // Skip sending
                    }
                    else
                    {
                        // Have Face.
                        // 2. If ALREADY TRACKING a Name, SKIP Sending! (UX Tip)
                        // Have Face.
                        // [NEW] TRACKING CHECK
                        // 2. Check if face matches Active Tracker (IoU > 0.5)
                        bool isTracked = false;
                        Rect normRect = _lastFaceRectNorm; // Updated via TryGetFaceDataFromLastResult

                        if (!string.IsNullOrEmpty(_activeTracker.studentId) && (Time.time - _activeTracker.lastSeenTime < TRACKING_TIMEOUT))
                        {
                            if (CalculateIoU(_activeTracker.lastRect, normRect) > TRACKING_IOU_THRESHOLD)
                            {
                                // TRACKING HIT!
                                isTracked = true;
                                _activeTracker.lastSeenTime = Time.time;
                                _activeTracker.lastRect = normRect;
                                
                                // Update UI locally without Server Request
                                if (_camLabel != null)
                                {
                                     _camLabel.text = $"{_activeTracker.studentName}\n{_activeTracker.matchPercent:F1}%"; // (Tracked)
                                     _camLabel.style.color = colGreen;
                                }
                                
                                // Reset cooldown to keep it responsive
                                _nextVerifyAllowedTime = 0; 
                            }
                        }

                        if (isTracked)
                        {
                             // [DEBUG]
                             // Debug.Log($"🔒 Tracking: {_activeTracker.studentName} (IoU Hit)");
                             _verifyInFlight = false; // Just to be safe
                             
                             // Optional: Still check for lock (simple lock)
                             if (!string.IsNullOrEmpty(_trackingName))
                             {
                                 // Already locked UI
                             }
                        }
                        else
                        {
                            // Not tracked -> Send Server Request
                            // [DEBUG]
                            // Debug.Log("📤 New Face / Lost Track -> Server Request...");

                            _verifyInFlight = true;
                            StartCoroutine(SendImageForRecognitionAsync());
                        }
                    }
                }
            }
            yield return new WaitForSeconds(verifyInterval); // [MOD] Use variable
        }
    }

    // ✅ NEW: Helper build JSON array cho multi-frame verify
    private string BuildVerifyPayload(List<string> b64List)
    {
        if (b64List == null || b64List.Count == 0) return "{}";
        if (b64List.Count == 1)
            return $"{{\"img_base64\":\"{b64List[0]}\"}}";

        var sb = new System.Text.StringBuilder();
        sb.Append("{\"imgs_base64\":[");
        for (int i = 0; i < b64List.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("\"").Append(b64List[i]).Append("\"");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private IEnumerator SendImageForRecognitionAsync()
    {
        // [DEBUG 1] Bắt đầu hàm
        Debug.Log($"🚀 [1] Bắt đầu quy trình gửi ảnh...");

        // 1. Kiểm tra URL
        if (string.IsNullOrEmpty(cloudRunApiUrl))
        {
            Debug.LogError("❌ LỖI: Link API trống!");
            _verifyInFlight = false;
            yield break;
        }

        // ✅ SINGLE-FRAME CAPTURE (giảm từ 2 xuống 1 — bỏ 1 lần gọi Modal, tiết kiệm ~3-5s)
        // MediaPipe đã filter mặt đủ rõ + backend filter confidence >= 0.90 nên 1 frame đủ tốt
        int frameCount = 1;
        float frameDelay = 0.05f;

        var framesB64 = new List<string>();

        for (int i = 0; i < frameCount; i++)
        {
            Texture2D t = null;
            try
            {
                t = CaptureFromMediaPipe();
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ LỖI CRASH KHI CROP (frame {i}): {ex.Message}");
            }

            if (t != null)
            {
                // Resize xuống max 320px để giảm payload gửi lên Modal
                // ArcFace chỉ cần 112x112, gửi ảnh lớn hơn chỉ lãng phí bandwidth
                int maxVerifyPx = 320;
                if (t.width > maxVerifyPx || t.height > maxVerifyPx)
                {
                    float scale = (float)maxVerifyPx / Mathf.Max(t.width, t.height);
                    int newW = Mathf.RoundToInt(t.width * scale);
                    int newH = Mathf.RoundToInt(t.height * scale);
                    Texture2D scaled = ScaleTexture(t, newW, newH);
                    Destroy(t);
                    t = scaled;
                }

                if (debugSaveJpg && i == 0)
                {
                    try
                    {
                        byte[] debugBytes = t.EncodeToJPG();
                        string debugPath = Path.Combine(Application.persistentDataPath, "debug_face.jpg");
                        File.WriteAllBytes(debugPath, debugBytes);
                        Debug.Log($"📸 [DEBUG] Saved first frame: {debugPath} ({t.width}x{t.height})");
                    }
                    catch { }
                }

                byte[] jpgBytes = t.EncodeToJPG(jpgQuality);
                string b64 = Convert.ToBase64String(jpgBytes);
                framesB64.Add(b64);

                Destroy(t);
            }

            if (i < frameCount - 1)
                yield return new WaitForSeconds(frameDelay);
        }

        if (framesB64.Count == 0)
        {
            Debug.LogError("❌ Không lấy được frame nào để verify!");
            _verifyInFlight = false;
            yield break;
        }

        string jsonPayload = BuildVerifyPayload(framesB64);
        byte[] rawBody = Encoding.UTF8.GetBytes(jsonPayload);

        string fullUrl = cloudRunApiUrl.TrimEnd('/') + "/verify";

        Debug.Log($"⏳ [3] Đang gửi {framesB64.Count} frames ({rawBody.Length} bytes) tới Server...");

        using (var webRequest = new UnityWebRequest(fullUrl, "POST"))
        {
            webRequest.uploadHandler = new UploadHandlerRaw(rawBody);
            webRequest.downloadHandler = new DownloadHandlerBuffer();
            webRequest.SetRequestHeader("Content-Type", "application/json");
            ApplyTenantHeaders(webRequest);
            webRequest.timeout = 60;

            double tStartNet = Time.realtimeSinceStartupAsDouble;
            yield return webRequest.SendWebRequest();
            double rttMs = (Time.realtimeSinceStartupAsDouble - tStartNet) * 1000.0;
            
            _latTotalMs = (float)rttMs + _latDetMs + _latAlignMs; // Rough Total: Client Process + RTT

            if (webRequest.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"❌ SERVER TRẢ LỖI: {webRequest.error} | {webRequest.downloadHandler.text}");
                // [NEW] Fail Cooldown (B)
                _nextVerifyAllowedTime = Time.realtimeSinceStartup + failCooldown;
                // Reset consecutive
                _candidateMssv = null;
                _candidateCount = 0;
            }
            else
            {
                // [DEBUG] In ra response thô để debug
                Debug.Log($"📨 Server Response: {webRequest.downloadHandler.text}");

                // [FIX] Safe JSON parsing with try/catch
                RecognitionResponse response = null;
                try
                {
                    response = JsonUtility.FromJson<RecognitionResponse>(webRequest.downloadHandler.text);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"❌ Parse JSON failed: {ex.Message}\nRAW={webRequest.downloadHandler.text}");
                    _nextVerifyAllowedTime = Time.realtimeSinceStartup + failCooldown;
                    _verifyInFlight = false;
                    yield break;
                }
                _lastVerifyResult = response;

                // [DEBUG] In chi tiết response đã parse
                Debug.Log($"📊 Parsed: status={response?.status}, name={response?.name}, mssv={response?.mssv}, match={response?.match_percent}%");

                // [NEW] Update Active Tracker on Success
                if (response != null && (response.status == "success" || response.status == "recognized"))
                {
                    _activeTracker.studentId = response.mssv;
                    _activeTracker.studentName = response.name;
                    _activeTracker.matchPercent = response.match_percent;
                    _activeTracker.lastSeenTime = Time.time;
                    _activeTracker.lastRect = _lastFaceRectNorm; // Use latest rect from when we sent? 
                    // Actually _lastFaceRectNorm might have changed during request. 
                    // But for tracking init, it's fine.
                }
                else
                {
                    // Reset if server says No Match
                    _activeTracker = new FaceTracker();
                }

                // [NEW] Update Latency Metrics
                if (response != null && response.latency_ms != null)
                {
                    _latDetMs = (faceDetector != null) ? faceDetector.LastDetectionMs : 0f;
                    _latAlignMs = _lastAlignMs;
                    _latEmbMs = response.latency_ms.embedding;
                    _latMatchMs = response.latency_ms.matching;
                    
                    float totalBackend = response.latency_ms.total_backend;
                    // Network = RTT - Backend processing time
                    _latNetMs = (float)rttMs - totalBackend;
                    if (_latNetMs < 0) _latNetMs = 0; // clamp
                    
                    // Re-calculate Total to be sum of components (for consistency) OR keep realized total
                    // Let's use sum of components to match the formula text
                    _latTotalMs = _latDetMs + _latAlignMs + _latEmbMs + _latMatchMs + _latNetMs;

                    UpdateLatencyCardUI();
                }

                bool isRecognized = (response != null && (response.status == "success" || response.status == "recognized"));

                // [NEW] LOGIC CONSECUTIVE HITS (C)
                if (isRecognized)
                {
                    // Check match percent
                    if (response.match_percent < minMatchPercentToCount)
                    {
                        // Coi như fail/low confidence
                        isRecognized = false;
                        Debug.LogWarning($"Low match percent: {response.match_percent}% < {minMatchPercentToCount}%");
                    }
                    else
                    {
                        // Logic đếm liên tiếp
                        float now = Time.realtimeSinceStartup;
                        if (response.mssv == _candidateMssv && (now - _candidateLastSeenTime) < 5.0f) // Timeout 5s giữa các hit (tăng từ 2s)
                        {
                            _candidateCount++;
                        }
                        else
                        {
                            _candidateMssv = response.mssv;
                            _candidateCount = 1;
                        }
                        _candidateLastSeenTime = now;

                        Debug.Log($"🎯 Candidate: {_candidateMssv} | Count: {_candidateCount}/{requiredConsecutiveHits}");

                        // Chưa đủ hit -> coi như chưa recognized để khỏi chốt sớm
                        if (_candidateCount < requiredConsecutiveHits)
                        {
                            isRecognized = false;
                        }
                    }
                }
                else
                {
                    // Not recognized -> Reset
                    _candidateMssv = null;
                    _candidateCount = 0;
                    // [NEW] Fail Cooldown
                    _nextVerifyAllowedTime = Time.realtimeSinceStartup + failCooldown;
                }

                // ============================================
                // CHỐT KẾT QUẢ CUỐI CÙNG
                // ============================================
                if (isRecognized)
                {
                    // debounce same MSSV for 10s (Log logic preserved)
                    long nowTick = DateTime.UtcNow.Ticks;
                    bool debounced = (response.mssv == _lastRecognizedMssv) &&
                                     (nowTick - _lastRecognizedTick < VERIFY_DEBOUNCE_TICKS);

                    if (!debounced)
                    {
                        _lastRecognizedTick = nowTick;
                        _lastRecognizedMssv = response.mssv ?? "";

                        // (Optional) lock tracking
                        _trackingName = response.name ?? "";

                        // Call Enqueue Log Immediately
                        EnqueueLogFromVerify(response);

                        // Turn off camera + fetch logs
                        StartCoroutine(ShowSuccessThenExit(response));

                        // Reset candidate logic after success? Optional.
                        _candidateCount = 0;
                    }

                    // Simple UI Update in the meantime
                    if (_camLabel != null)
                    {
                        _camLabel.text = $"{response.name}\n{response.match_percent:F1}%";
                        _camLabel.style.color = colGreen;
                    }
                }
                else
                {
                    if (_camLabel != null)
                    {
                        var failMsg = response?.status == "not_recognized" ? "Unknown" : "Scanning...";
                        _camLabel.text = failMsg;
                        _camLabel.style.color = Color.yellow;
                    }
                }
            }
        }

        _verifyInFlight = false;
    }
    void OnAddFaceClicked()
    {
        // 1. Check Previous State
        bool wasRunningBefore = _isCameraRunning;

        // 2. Đảm bảo Camera AI ĐANG CHẠY (nhưng KHÔNG tự bật logic nhận diện)
        if (!_isCameraRunning) SetCameraDriver(true);

        // Pause gửi ảnh API
        if (_recognitionCoroutine != null)
        {
            StopCoroutine(_recognitionCoroutine);
            _recognitionCoroutine = null;
        }

        // P0 FIX: Hide Recognition Camera (Center) to avoid overlap
        if (_cameraMainContainer != null) _cameraMainContainer.style.display = DisplayStyle.None;

        // Cleanup ảnh cũ
        if (_capturedFront != null) Destroy(_capturedFront);
        if (_capturedLeft != null) Destroy(_capturedLeft);
        if (_capturedRight != null) Destroy(_capturedRight);
        _capturedFront = _capturedLeft = _capturedRight = null;

        // 2. Tạo Overlay Fullscreen
        var overlay = new VisualElement();
        overlay.style.position = Position.Absolute;
        overlay.style.top = 0; overlay.style.bottom = 0;
        overlay.style.left = 0; overlay.style.right = 0;
        overlay.style.backgroundColor = new Color(0, 0, 0, 0.4f);
        overlay.style.justifyContent = Justify.Center;
        overlay.style.alignItems = Align.Center;

        var box = new VisualElement();
        box.style.backgroundColor = Color.white;
        box.style.width = 420;
        box.style.paddingLeft = 20; box.style.paddingRight = 20;
        box.style.paddingTop = 20; box.style.paddingBottom = 20;
        box.style.borderTopLeftRadius = 15; box.style.borderTopRightRadius = 15;
        box.style.borderBottomLeftRadius = 15; box.style.borderBottomRightRadius = 15;
        box.style.alignItems = Align.Center;

        overlay.Add(box);
        _root.Add(overlay);

        // Bắt đầu quy trình Training Logic (Pass wasRunningBefore)
        StartCoroutine(ProcessTrainingFlow(overlay, box, wasRunningBefore));
    }

    // ✅ NEW: Helper đóng modal dùng chung (tránh bị block input)
    private void CloseRegisterModal(VisualElement overlay, bool wasRunningBefore)
    {
        // Stop training coroutine
        if (_trainingCoroutine != null)
        {
            StopCoroutine(_trainingCoroutine);
            _trainingCoroutine = null;
        }

        // Remove overlay if still exists
        try
        {
            if (overlay != null && overlay.parent != null)
                overlay.RemoveFromHierarchy();
        }
        catch { }

        // IMPORTANT: hide + clear modal container so it won't block clicks
        if (_modalContainer != null)
        {
            _modalContainer.Clear();
            _modalContainer.style.display = DisplayStyle.None;
        }

        // Turn off camera driver used for register capture
        SetCameraDriver(false);

        // Cleanup captured textures (avoid memory leak)
        if (_capturedFront != null) { Destroy(_capturedFront); _capturedFront = null; }
        if (_capturedLeft != null) { Destroy(_capturedLeft); _capturedLeft = null; }
        if (_capturedRight != null) { Destroy(_capturedRight); _capturedRight = null; }

        // Restore recognition if it was running before opening register
        if (wasRunningBefore)
        {
            if (_cameraMainContainer != null)
                _cameraMainContainer.style.display = DisplayStyle.Flex;

            if (_recognitionCoroutine != null)
                StopCoroutine(_recognitionCoroutine);

            _recognitionCoroutine = StartCoroutine(RecognitionLoop());
        }
    }

    // --- [MỚI] GỬI REQUEST ĐĂNG KÝ (3 ẢNH) + RETRY & RESIZE & APPEND MODE ---
    IEnumerator SendRegisterRequest(List<Texture2D> images, VisualElement overlay, VisualElement box, bool wasRunningBefore, bool appendMode)
    {
        var lblInstruction = box.Q<Label>("LblInstruction");
        if(lblInstruction != null) lblInstruction.text = "Đang xử lý ảnh...";

        WWWForm form = new WWWForm();
        
        // Thêm các trường Text
        form.AddField("student_id", _regMssv);
        form.AddField("mssv", _regMssv);
        form.AddField("name", _regName);
        
        // [NEW] Append Mode (Hybrid Registration)
        if (appendMode)
        {
             form.AddField("append_mode", "true");
        }

        // --- ĐÓNG GÓI 3 ẢNH (Auto Resize) ---
        for (int i = 0; i < images.Count; i++)
        {
            Texture2D tex = images[i];
            
            // [NEW] Resize if too big (> 512px)
            if (tex.width > 512 || tex.height > 512)
            {
                // Calculate new size maintaining aspect ratio
                float ratio = (float)tex.width / tex.height;
                int newW = 512;
                int newH = (int)(512 / ratio);
                if (tex.height > tex.width)
                {
                    newH = 512;
                    newW = (int)(512 * ratio);
                }
                
                Texture2D scaled = ScaleTexture(tex, newW, newH);
                tex = scaled; // Use scaled one
            }

            // Mã hóa thành JPG
            byte[] bytes = tex.EncodeToJPG(75); // Quality 75%
            
            // Clean up temporary scaled texture if created
            if (tex != images[i]) Destroy(tex);

            form.AddBinaryData("images", bytes, $"face_{i}.jpg", "image/jpeg");
        }

        if(lblInstruction != null) lblInstruction.text = "Đang gửi dữ liệu (0/3)...";

        // [NEW] RETRY LOGIC
        int maxRetries = 3;
        int currentTry = 0;
        bool success = false;

        while (currentTry < maxRetries && !success)
        {
            currentTry++;
            if(lblInstruction != null) lblInstruction.text = $"Đang gửi... (Thử {currentTry}/{maxRetries})";

            using (UnityWebRequest www = UnityWebRequest.Post(cloudRunApiUrl + "/register", form))
            {
                ApplyTenantHeaders(www);
                www.timeout = 60; // Tăng timeout cho mạng yếu
                
                yield return www.SendWebRequest();

                if (www.result == UnityWebRequest.Result.Success)
                {
                    success = true;
                    string responseText = www.downloadHandler.text;
                    Debug.Log("Server phản hồi: " + responseText);

                    if(lblInstruction != null) lblInstruction.text = "✅ ĐĂNG KÝ THÀNH CÔNG!";

                    // Thêm sinh viên vào danh sách lớp nếu chưa có
                    if (_studentDataList != null)
                    {
                        string normMssv = (_regMssv ?? "").Trim();
                        bool exists = _studentDataList.Any(s => (s.MSSV ?? "").Trim() == normMssv);
                        if (!exists && !string.IsNullOrEmpty(normMssv))
                        {
                            _studentDataList.Add(new StudentStat
                            {
                                Name = _regName ?? "",
                                MSSV = normMssv,
                                IsRegistered = true,
                                TotalCheckIn = 0, LateCount = 0, OnTimeCount = 0, AbsentCount = 0
                            });
                            SaveStudentDataListToFile();
                            StartCoroutine(PushRosterToServer(silent: true));
                        }
                        else if (exists)
                        {
                            var st = _studentDataList.FirstOrDefault(s => (s.MSSV ?? "").Trim() == normMssv);
                            if (st != null) st.IsRegistered = true;
                        }
                    }

                    yield return new WaitForSeconds(1.5f);
                    CloseRegisterModal(overlay, wasRunningBefore);
                    FetchAndRenderStudentList();
                }
                else
                {
                    Debug.LogError(
                        $"Register failed (Attempt {currentTry}) | code={www.responseCode} | error={www.error}\n" +
                        $"Body: {www.downloadHandler?.text}"
                    );

                    if (currentTry < maxRetries)
                    {
                        if(lblInstruction != null) lblInstruction.text = $"Lỗi mạng. Thử lại sau 2s...";
                        yield return new WaitForSeconds(2.0f);
                    }
                    else
                    {
                        if(lblInstruction != null) lblInstruction.text = $"Thất bại: {www.error}.";
                        // Enable Cancel button to let user close
                         var btnCancel = box.Q<Button>(); // Hacky find, but structure is simple
                         if(btnCancel != null) btnCancel.SetEnabled(true);
                    }
                }
            }
        }
    }

    // --- HELPER: SCALE TEXTURE ---
    Texture2D ScaleTexture(Texture2D source, int targetWidth, int targetHeight)
    {
        RenderTexture rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.Default, RenderTextureReadWrite.Linear);
        Graphics.Blit(source, rt);
        
        Texture2D result = new Texture2D(targetWidth, targetHeight);
        RenderTexture.active = rt;
        result.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
        result.Apply();
        
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(rt);
        return result;
    }

    // --- [THÊM MỚI] UI & LOGIC 3 BƯỚC (AUTO-CAPTURE) ---
    IEnumerator ProcessTrainingFlow(VisualElement overlay, VisualElement box, bool wasRunningBefore)
    {
        // === SETUP UI TRAINING ===
        box.Clear();

        // Nút Close "X"
        var btnClose = new Button();
        btnClose.text = "✖";
        btnClose.style.position = Position.Absolute;
        btnClose.style.top = 10; btnClose.style.right = 10;
        btnClose.style.width = 30; btnClose.style.height = 30;
        btnClose.style.backgroundColor = Color.clear;
        btnClose.style.color = colTextDark;
        btnClose.style.borderTopWidth = 0;
        btnClose.style.borderBottomWidth = 0;
        btnClose.style.borderLeftWidth = 0;
        btnClose.style.borderRightWidth = 0;
        btnClose.style.fontSize = 18;
        btnClose.style.unityFontStyleAndWeight = FontStyle.Bold;
        btnClose.clicked += () => CloseRegisterModal(overlay, wasRunningBefore);
        box.Add(btnClose);

        var lblTitle = new Label("ĐĂNG KÝ KHUÔN MẶT");
        lblTitle.style.fontSize = 20;
        lblTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
        lblTitle.style.marginBottom = 10;
        lblTitle.style.color = colTextDark;
        box.Add(lblTitle);

        // Instruction Text (Sẽ đổi theo step)
        var lblInstruction = new Label("Đang chuẩn bị...");
        lblInstruction.name = "LblInstruction";
        lblInstruction.style.fontSize = 16;
        lblInstruction.style.color = colAccent;
        lblInstruction.style.marginBottom = 10;
        lblInstruction.style.whiteSpace = WhiteSpace.Normal;
        box.Add(lblInstruction);

        // [NEW] Toggle Mask Support (Append Mode)
        var tglMask = new Toggle("Đeo khẩu trang? (Đăng ký thêm)");
        tglMask.style.marginTop = 5;
        tglMask.style.marginBottom = 10;
        tglMask.style.color = colTextDark;
        // Style checkmark if needed, but default is okay
        box.Add(tglMask);

        // Camera Preview Box
        var camPreview = new VisualElement();
        camPreview.style.width = 320; camPreview.style.height = 240;
        camPreview.style.backgroundColor = Color.black;
        camPreview.style.marginBottom = 15;
        camPreview.style.overflow = Overflow.Hidden; // Crop if needed
        box.Add(camPreview);

        // Face Bounding Box (Visual Feedback)
        var faceBox = new VisualElement();
        faceBox.style.position = Position.Absolute;
        faceBox.style.borderTopWidth = 2; faceBox.style.borderBottomWidth = 2;
        faceBox.style.borderLeftWidth = 2; faceBox.style.borderRightWidth = 2;
        faceBox.style.borderTopColor = Color.red; faceBox.style.borderBottomColor = Color.red;
        faceBox.style.borderLeftColor = Color.red; faceBox.style.borderRightColor = Color.red;
        faceBox.style.display = DisplayStyle.None; // Hide ban đầu
        camPreview.Add(faceBox);

        // Nút Hủy
        var btnCancel = new Button(() => CloseRegisterModal(overlay, wasRunningBefore)) { text = "Hủy Bỏ" };
        btnCancel.style.marginTop = 10;
        btnCancel.style.width = 100;
        btnCancel.style.backgroundColor = Color.grey;
        box.Add(btnCancel);

        // Reset Data
        _registerStep = 0;
        _tempRegisterImages.Clear();

        // Start Auto-Capture Sequence
        // Step 1: Front
        _registerStep = 1;
        yield return StartCoroutine(WaitForPose(overlay, lblInstruction, camPreview, faceBox, "NHÌN THẲNG", 0, 20, (tex) => _tempRegisterImages.Add(tex)));
        if(overlay.parent == null) yield break; // User closed

        // Step 2: Left
        _registerStep = 2;
        yield return StartCoroutine(WaitForPose(overlay, lblInstruction, camPreview, faceBox, "QUAY TRÁI", -45, -15, (tex) => _tempRegisterImages.Add(tex)));
        if(overlay.parent == null) yield break;

        // Step 3: Right
        _registerStep = 3;
        yield return StartCoroutine(WaitForPose(overlay, lblInstruction, camPreview, faceBox, "QUAY PHẢI", 15, 45, (tex) => _tempRegisterImages.Add(tex)));
        if(overlay.parent == null) yield break;

        // DONE -> Lưu ảnh chụp để ShowInputDetailsOverlay hiện thumbnail
        _capturedFront = _tempRegisterImages.Count > 0 ? _tempRegisterImages[0] : null;
        _capturedLeft  = _tempRegisterImages.Count > 1 ? _tempRegisterImages[1] : null;
        _capturedRight = _tempRegisterImages.Count > 2 ? _tempRegisterImages[2] : null;

        bool isAppend = tglMask != null && tglMask.value;

        // Nếu chưa có tên/MSSV (flow từ nút đăng ký ở Dashboard) → hiện form nhập trước
        if (string.IsNullOrEmpty(_regName) || string.IsNullOrEmpty(_regMssv))
        {
            ShowInputDetailsOverlay(overlay, box, wasRunningBefore);
        }
        else
        {
            // Đã có thông tin (flow từ Register Tab / danh sách) → gửi luôn
            lblInstruction.text = "Hoàn tất! Đang gửi dữ liệu...";
            lblInstruction.style.color = colBlue;
            btnCancel.SetEnabled(false);
            StartCoroutine(SendRegisterRequest(_tempRegisterImages, overlay, box, wasRunningBefore, isAppend));
        }
    }

    // --- HELPER: AUTO-CAPTURE WAITER ---
    IEnumerator WaitForPose(VisualElement overlay, Label lblInstr, VisualElement camPreview, VisualElement faceBox, string poseName, float minYaw, float maxYaw, Action<Texture2D> onCapture)
    {
        lblInstr.text = $"{poseName}...";
        lblInstr.style.color = colAccent;
        
        float holdTimer = 0f;
        float requiredTime = 0.5f; // Giữ 0.5s để chụp
        bool captured = false;
        
        // Loop cho đến khi chụp được hoặc user đóng
        while (!captured && overlay.parent != null)
        {
            // [FIX] Actively capture frame for preview (since main loop is paused)
            Texture2D newTex = CaptureFullFrame();
            if (newTex != null)
            {
                if (_capTex != null) Destroy(_capTex);
                _capTex = newTex;
                camPreview.style.backgroundImage = new StyleBackground(_capTex);
            }

            // Lấy thông tin mặt từ MediaPipe (Hàm có sẵn trong file)
            if (TryGetFaceDataFromLastResult(out Rect r, out float yaw, out bool hasFace))
            {
                // Update Bounding Box UI
                faceBox.style.display = DisplayStyle.Flex;
                float w = 320; float h = 240;
                faceBox.style.left = r.x * w; 
                faceBox.style.top = r.y * h;
                faceBox.style.width = r.width * w; 
                faceBox.style.height = r.height * h;

                // Check Pose Condition
                // Logic: 
                // Front: |yaw| < 20
                // Left: yaw < -15 (max) && yaw > -45 (min)
                // Right: yaw > 15 (min) && yaw < 45 (max)
                
                bool isCorrectPose = false;
                if (minYaw == 0) // Front case logic simplified
                    isCorrectPose = Mathf.Abs(yaw) < maxYaw; // |Yaw| < 20
                else if (maxYaw < 0) // Left case (Negative)
                    isCorrectPose = (yaw < maxYaw && yaw > minYaw);
                else // Right case (Positive)
                    isCorrectPose = (yaw > minYaw && yaw < maxYaw);

                if (isCorrectPose)
                {
                    holdTimer += Time.deltaTime;
                    faceBox.style.borderTopColor = Color.green; faceBox.style.borderBottomColor = Color.green;
                    faceBox.style.borderLeftColor = Color.green; faceBox.style.borderRightColor = Color.green;
                    
                    lblInstr.text = $"{poseName} OK! ({holdTimer:F1}s)";
                    lblInstr.style.color = colGreen;

                    if (holdTimer >= requiredTime)
                    {
                        // CAPTURE!
                        Texture2D fullFrame = CaptureCurrentFrame();
                        if (fullFrame != null)
                        {
                            // Use full frame directly (CropFaceFromTexture not available)
                            onCapture(fullFrame);
                            captured = true;
                            // Visual Feedback
                            ShowToast($"✅ Đã chụp: {poseName}", colGreen);
                            yield return new WaitForSeconds(0.5f); // Delay chút trước khi sang step sau
                        }
                    }
                }
                else
                {
                    holdTimer = 0f;
                    faceBox.style.borderTopColor = Color.red; faceBox.style.borderBottomColor = Color.red;
                    faceBox.style.borderLeftColor = Color.red; faceBox.style.borderRightColor = Color.red;
                    
                    lblInstr.text = $"Hãy {poseName} (Yaw: {yaw:F0})";
                    lblInstr.style.color = colOrange;
                }
            }
            else
            {
                faceBox.style.display = DisplayStyle.None;
                lblInstr.text = "Không thấy khuôn mặt...";
                lblInstr.style.color = colRed;
            }

            yield return null; // Wait next frame
        }
    }


    void ShowInputDetailsOverlay(VisualElement overlay, VisualElement box, bool wasRunningBefore)
    {
        box.Clear();
        box.style.width = 400;

        var lblTitle = new Label("XÁC NHẬN THÔNG TIN");
        lblTitle.style.fontSize = 20;
        lblTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
        lblTitle.style.marginBottom = 15;
        lblTitle.style.color = colTextDark;
        box.Add(lblTitle);

        // Hiện Thumbnails 3 góc (Front, Left, Right)
        var thumbsRow = new VisualElement();
        thumbsRow.style.flexDirection = FlexDirection.Row;
        thumbsRow.style.justifyContent = Justify.Center;
        thumbsRow.style.marginBottom = 15;
        box.Add(thumbsRow);

        Action<Texture2D, string> AddThumb = (tex, label) =>
        {
            if (tex == null) return;
            var container = new VisualElement();
            container.style.alignItems = Align.Center;
            container.style.marginRight = 5; container.style.marginLeft = 5;

            var t = new VisualElement();
            t.style.width = 80; t.style.height = 80;
            t.style.borderTopLeftRadius = 40; t.style.borderTopRightRadius = 40;
            t.style.borderBottomLeftRadius = 40; t.style.borderBottomRightRadius = 40;
            t.style.overflow = Overflow.Hidden;
            t.style.backgroundImage = new StyleBackground(tex);
            t.style.borderBottomWidth = 2; t.style.borderBottomColor = colAccent;
            t.style.borderTopWidth = 2; t.style.borderTopColor = colAccent;
            t.style.borderLeftWidth = 2; t.style.borderLeftColor = colAccent;
            t.style.borderRightWidth = 2; t.style.borderRightColor = colAccent;

            container.Add(t);

            var lbl = new Label(label);
            lbl.style.fontSize = 12;
            lbl.style.color = colTextDark;
            container.Add(lbl);

            thumbsRow.Add(container);
        };

        AddThumb(_capturedFront, "Thẳng");
        AddThumb(_capturedLeft, "Trái");
        AddThumb(_capturedRight, "Phải");

        var txtName = new TextField("Tên sinh viên");
        txtName.style.width = 300;
        txtName.style.marginBottom = 10;
        txtName.isDelayed = true; // [FIX] Prevent crash on Backspace/IME
        if (!string.IsNullOrEmpty(_regName)) txtName.value = _regName;

        box.Add(txtName);

        var txtMssv = new TextField("MSSV");
        txtMssv.style.width = 300;
        txtMssv.style.marginBottom = 20;
        txtMssv.isDelayed = true; // [FIX] Prevent crash on Backspace/IME
        if (!string.IsNullOrEmpty(_regMssv)) txtMssv.value = _regMssv;
        box.Add(txtMssv);

        var btnContainer = new VisualElement();
        btnContainer.style.flexDirection = FlexDirection.Row;
        btnContainer.style.width = 300;
        btnContainer.style.justifyContent = Justify.SpaceBetween;

        // ✅ FIX: Use CloseRegisterModal for proper cleanup
        var btnCancel = new Button(() =>
        {
            CloseRegisterModal(overlay, wasRunningBefore);
        })
        { text = "Hủy" };
        btnCancel.style.width = 100;
        btnContainer.Add(btnCancel);

        var btnSave = new Button();
        btnSave.text = "LƯU DỮ LIỆU";
        btnSave.style.flexGrow = 1;
        btnSave.style.marginLeft = 10;
        btnSave.style.backgroundColor = colAccent;
        btnSave.style.color = Color.white;
        btnSave.style.unityFontStyleAndWeight = FontStyle.Bold;

        btnSave.clicked += () =>
        {
            if (string.IsNullOrEmpty(txtName.value) || string.IsNullOrEmpty(txtMssv.value))
            {
                ShowToast("Vui lòng nhập đủ thông tin!", colRed);
                return;
            }

            btnSave.SetEnabled(false);
            btnSave.text = "Đang lưu...";

            // ✅ FIX: Use CloseRegisterModal in onComplete callback
            StartCoroutine(ProcessRegisterWithImage(txtName.value, txtMssv.value, () =>
            {
                CloseRegisterModal(overlay, wasRunningBefore);
            }));
        };
        btnContainer.Add(btnSave);
        box.Add(btnContainer);
    }

    IEnumerator ProcessRegisterWithImage(string name, string mssv, Action onComplete)
    {
        ShowToast("☁️ Đang đồng bộ dữ liệu...", colAccent);
        yield return null;

        // 1. PHASE 1: PREPARE DATA (Synchronous - Can use Try-Catch)
        string b64Front = null, b64Left = null, b64Right = null;
        bool prepareSuccess = false;
        string errorMsg = "";

        try
        {
            if (_capturedFront != null)
            {
                var jpg = _capturedFront.EncodeToJPG(90);
                if (jpg == null) throw new Exception("Lỗi xử lý ảnh Chính diện");
                b64Front = Convert.ToBase64String(jpg);
            }

            if (_capturedLeft != null)
            {
                var jpg = _capturedLeft.EncodeToJPG(90);
                if (jpg == null) throw new Exception("Lỗi xử lý ảnh Trái");
                b64Left = Convert.ToBase64String(jpg);
            }

            if (_capturedRight != null)
            {
                var jpg = _capturedRight.EncodeToJPG(90);
                if (jpg == null) throw new Exception("Lỗi xử lý ảnh Phải");
                b64Right = Convert.ToBase64String(jpg);
            }
            prepareSuccess = true;
        }
        catch (Exception ex)
        {
            errorMsg = ex.Message;
            Debug.LogError($"[Register Error] Prepare failed: {ex}");
        }

        // 2. ERROR HANDLING (If prepare failed)
        if (!prepareSuccess)
        {
            ShowToast($"❌ Lỗi: {errorMsg}", colRed);
            yield return new WaitForSeconds(2.0f);
            onComplete?.Invoke();
            yield break; // Exit
        }

        // 3. PHASE 2: UPLOAD (Asynchronous)
        // [FIX] Use try-finally (without catch) to handle cleanup even if Coroutine crashes
        try
        {
            int totalCount = 0;
            if (b64Front != null) totalCount++;
            if (b64Left != null) totalCount++;
            if (b64Right != null) totalCount++;

            int current = 0;

            // Upload Front
            if (b64Front != null)
            {
                current++;
                ShowToast($"Uploading Front ({current}/{totalCount})...", colBlue);
                yield return StartCoroutine(PostRegister(b64Front, name, mssv, reloadIndex: (current == totalCount)));
                if (!_lastRegisterOk)
                {
                    ShowToast($"❌ Đăng ký thất bại: {_lastRegisterErr}", colRed);
                    yield return new WaitForSeconds(1.0f);
                    yield break;
                }
            }

            // Upload Left
            if (b64Left != null)
            {
                current++;
                ShowToast($"Uploading Left ({current}/{totalCount})...", colBlue);
                yield return StartCoroutine(PostRegister(b64Left, name, mssv, reloadIndex: (current == totalCount)));
                if (!_lastRegisterOk)
                {
                    ShowToast($"❌ Đăng ký thất bại: {_lastRegisterErr}", colRed);
                    yield return new WaitForSeconds(1.0f);
                    yield break;
                }
            }

            // Upload Right
            if (b64Right != null)
            {
                current++;
                ShowToast($"Uploading Right ({current}/{totalCount})...", colBlue);
                yield return StartCoroutine(PostRegister(b64Right, name, mssv, reloadIndex: (current == totalCount)));
                if (!_lastRegisterOk)
                {
                    ShowToast($"❌ Đăng ký thất bại: {_lastRegisterErr}", colRed);
                    yield return new WaitForSeconds(1.0f);
                    yield break;
                }
            }

            // 4. FINISH
            ShowToast("✅ Đã thêm dữ liệu khuôn mặt!", colGreen);

            // Thêm sinh viên vào danh sách lớp nếu chưa có (đăng ký tay người mới)
            if (_studentDataList != null)
            {
                string normMssv = (mssv ?? "").Trim();
                bool exists = _studentDataList.Any(s => (s.MSSV ?? "").Trim() == normMssv);
                if (!exists && !string.IsNullOrEmpty(normMssv))
                {
                    _studentDataList.Add(new StudentStat
                    {
                        Name = name ?? "",
                        MSSV = normMssv,
                        IsRegistered = true,
                        TotalCheckIn = 0,
                        LateCount = 0,
                        OnTimeCount = 0,
                        AbsentCount = 0
                    });
                    SaveStudentDataListToFile();
                    StartCoroutine(PushRosterToServer(silent: true));
                    Debug.Log($"[Register] Added new student to roster: {name} ({normMssv})");
                }
                else if (exists)
                {
                    // Đánh dấu đã đăng ký
                    var st = _studentDataList.FirstOrDefault(s => (s.MSSV ?? "").Trim() == normMssv);
                    if (st != null) st.IsRegistered = true;
                }
            }

            // ✅ [OPT] Refresh profiles ONCE (avoid duplicated fetch)
            FaceProfile[] profiles = null;
            yield return StartCoroutine(GetProfiles(p => profiles = p));
            _allProfilesCache = profiles?.ToList() ?? new List<FaceProfile>();

            // Tell register tab to reuse cache (skip next fetch)
            _skipNextProfilesFetchInRegisterTab = true;

            // Refresh UI (using cached profiles)
            RenderRegisterTabContent();
            FilterStudentList(_inpSearchStudent?.value ?? "");
            UpdateStatsFromLogs();
            UpdateTrendChart();
            UpdateStatsTab();

            yield return new WaitForSeconds(1.0f);
        }
        finally
        {
            // [CRITICAL] Always remove overlay to prevent UI freeze
            onComplete?.Invoke();
        }
    }
    // =========================================================
    // HÀM GỌI API RELOAD (Cập nhật dữ liệu Server)
    // =========================================================
    // Flag chặn verify khi đang reload
    private bool _isReloading = false;

    // =========================================================
    // HÀM GỌI API RELOAD (Cập nhật dữ liệu Server)
    // =========================================================
    void ReloadFaissIndex()
    {
        string reloadUrl = cloudRunApiUrl.TrimEnd('/') + "/reload";
        StartCoroutine(ReloadRequest(reloadUrl));
    }

    IEnumerator ReloadRequest(string url)
    {
        _isReloading = true; // Lock verification
        ShowToast("🔄 Đang cập nhật dữ liệu Server...", colAccent);

        using (UnityWebRequest webRequest = UnityWebRequest.Get(url))
        {
            ApplyTenantHeaders(webRequest);
            Debug.Log($"🔄 Đang gọi API: {url}");
            yield return webRequest.SendWebRequest();

            if (webRequest.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("✅ Server phản hồi: " + webRequest.downloadHandler.text);
                ShowToast("✅ Cập nhật thành công! Có thể điểm danh ngay.", colGreen);
            }
            else
            {
                Debug.LogError($"❌ Lỗi Reload: {webRequest.error}");
                ShowToast("❌ Lỗi cập nhật Server!", colRed);
            }
        }
        _isReloading = false; // Unlock
    }

    // Hàm phụ để chờ và reload
    IEnumerator WaitAndReload(float delay)
    {
        Debug.Log($"⏳ Chờ {delay} giây để bạn đăng ký khuôn mặt...");
        yield return new WaitForSeconds(delay);

        // Sau khi chờ xong, gọi lệnh reload
        Debug.Log("🔄 Đang gửi lệnh cập nhật Server...");
        ShowToast("🔄 Đang cập nhật dữ liệu mới lên Server...", colAccent);

        // Gọi hàm ReloadFaissIndex (bạn đã viết ở bước trước)
        // Lưu ý: Đảm bảo bạn đã có hàm ReloadFaissIndex() trong code
        ReloadFaissIndex();
    }
    // --- TẠO MÀN HÌNH CHÀO (SPLASH SCREEN) - FINAL FIX ---
    void CreateSplashScreen()
    {
        // 1. Tạo container nền trắng phủ kín màn hình
        var splash = new VisualElement();
        splash.name = "SplashScreen";
        splash.style.position = Position.Absolute;
        splash.style.top = 0; splash.style.bottom = 0;
        splash.style.left = 0; splash.style.right = 0;
        splash.style.backgroundColor = Color.white;
        splash.style.justifyContent = Justify.Center;
        splash.style.alignItems = Align.Center;

        // Biến để giữ phần tử chính (ảnh hoặc chữ) để lát nữa gắn animation
        VisualElement mainContent = null;

        // 2. HIỂN THỊ LOGO (Dùng biến Inspector)
        if (schoolLogo != null)
        {
            var imgLogo = new VisualElement();
            imgLogo.style.backgroundImage = new StyleBackground(schoolLogo);
            imgLogo.style.width = 300;
            imgLogo.style.height = 300;
            imgLogo.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);

            splash.Add(imgLogo);
            mainContent = imgLogo; // Lưu lại để animate
        }
        else
        {
            // Dự phòng nếu quên kéo ảnh
            var lblSchool = new Label("TRƯỜNG ĐH SPKT TP.HCM");
            lblSchool.style.fontSize = 30;
            lblSchool.style.unityFontStyleAndWeight = FontStyle.Bold;
            lblSchool.style.color = new Color(0.1f, 0.2f, 0.5f);
            lblSchool.style.unityTextAlign = TextAnchor.MiddleCenter;

            splash.Add(lblSchool);
            mainContent = lblSchool; // Lưu lại để animate
        }

        // ==============================================================================
        // --- BẮT ĐẦU SETUP ANIMATION CHO LOGO (CODE MỚI) ---
        if (mainContent != null)
        {
            // A. Trạng thái ban đầu: Ẩn đi và nhỏ lại 50%
            mainContent.style.opacity = 0;
            mainContent.style.scale = new Scale(new Vector2(0.5f, 0.5f));

            // B. Định nghĩa cách chuyển động
            // Chúng ta muốn thay đổi 2 thuộc tính: độ mờ (opacity) và tỉ lệ (scale)
            mainContent.style.transitionProperty = new List<StylePropertyName> {
                new StylePropertyName("opacity"),
                new StylePropertyName("scale")
            };
            // Thời gian chạy là 0.8 giây cho cả 2
            mainContent.style.transitionDuration = new List<TimeValue> {
                new TimeValue(0.8f),
                new TimeValue(0.8f)
            };
            // Kiểu chuyển động mượt mà (bắt đầu nhanh, kết thúc chậm dần)
            mainContent.style.transitionTimingFunction = new List<EasingFunction> { EasingMode.EaseOutCubic };

            // C. Kích hoạt: Đợi 100ms sau khi hiện màn hình thì bắt đầu chạy hiệu ứng
            splash.schedule.Execute(() =>
            {
                // Đưa về trạng thái cuối cùng: Hiện rõ 100% và kích thước chuẩn
                mainContent.style.opacity = 1;
                mainContent.style.scale = new Scale(new Vector2(1f, 1f));
            }).StartingIn(100); // Delay 100ms
        }
        // ==============================================================================


        // Tên phần mềm (cái này giữ nguyên không animate cho đỡ rối)
        var lblApp = new Label("HỆ THỐNG ĐIỂM DANH THÔNG MINH");
        lblApp.style.marginTop = 20;
        lblApp.style.marginBottom = 50;
        lblApp.style.fontSize = 16;
        lblApp.style.color = Color.gray;
        splash.Add(lblApp);

        // --- 3. TẠO NÚT BẮT ĐẦU ---
        var btnStart = new Button();
        btnStart.text = "Start";

        // Trang trí nút
        btnStart.style.width = 220;
        btnStart.style.height = 55;
        btnStart.style.backgroundColor = new Color(0.2f, 0.6f, 0.3f); // Xanh lá đậm
        btnStart.style.color = Color.white;
        btnStart.style.fontSize = 18;
        btnStart.style.unityFontStyleAndWeight = FontStyle.Bold;
        // Bo tròn
        btnStart.style.borderTopLeftRadius = 30; btnStart.style.borderTopRightRadius = 30;
        btnStart.style.borderBottomLeftRadius = 30; btnStart.style.borderBottomRightRadius = 30;
        // Thay dòng btnStart.style.borderWidth = 0; bằng đoạn này:
        btnStart.style.borderTopWidth = 0;
        btnStart.style.borderBottomWidth = 0;
        btnStart.style.borderLeftWidth = 0;
        btnStart.style.borderRightWidth = 0;

        // Hiệu ứng Hover nút
        btnStart.RegisterCallback<MouseEnterEvent>(e => btnStart.style.backgroundColor = new Color(0.25f, 0.7f, 0.35f));
        btnStart.RegisterCallback<MouseLeaveEvent>(e => btnStart.style.backgroundColor = new Color(0.2f, 0.6f, 0.3f));

        // --- SỰ KIỆN BẤM NÚT ĐỂ VÀO MENU CHÍNH ---
        btnStart.clicked += () =>
        {
            btnStart.text = "Đang truy cập...";
            btnStart.SetEnabled(false);

            // Hiệu ứng mờ dần toàn bộ màn hình chào khi bấm nút
            splash.style.transitionProperty = new List<StylePropertyName> { new StylePropertyName("opacity") };
            splash.style.transitionDuration = new List<TimeValue> { new TimeValue(0.5f) };
            splash.style.transitionTimingFunction = new List<EasingFunction> { EasingMode.EaseOut };

            splash.style.opacity = 0; // Bắt đầu mờ

            splash.schedule.Execute(() =>
            {
                if (_root.Contains(splash)) _root.Remove(splash);
            }).StartingIn(500);
        };

        splash.Add(btnStart);

        // Thêm vào giao diện
        _root.Add(splash);
    }
    // ==================================================================================
    // --- TÍNH NĂNG QUẢN LÝ ẢNH (DATA MANAGER) ---
    // ==================================================================================

    void ShowPhotoManager()
    {
        // 1. Định nghĩa đường dẫn tới thư mục photos
        // Lưu ý: Sửa lại đường dẫn này nếu thư mục photos của bạn nằm chỗ khác
        string photosRootPath = Path.Combine(Application.streamingAssetsPath, "FaceAI", "Recognition", "photos");

        // 2. Tạo giao diện nền (Overlay)
        var overlay = new VisualElement();
        overlay.style.position = Position.Absolute;
        overlay.style.top = 0; overlay.style.bottom = 0;
        overlay.style.left = 0; overlay.style.right = 0;
        overlay.style.backgroundColor = new Color(0, 0, 0, 0.9f); // Nền đen mờ đậm
        overlay.style.paddingTop = 20; overlay.style.paddingBottom = 20;
        overlay.style.paddingLeft = 20; overlay.style.paddingRight = 20;

        // 3. Tiêu đề và nút Đóng
        var header = new VisualElement();
        header.style.flexDirection = FlexDirection.Row;
        header.style.justifyContent = Justify.SpaceBetween;

        var lblTitle = new Label("QUẢN LÝ DỮ LIỆU KHUÔN MẶT");
        lblTitle.style.fontSize = 20;
        lblTitle.style.color = Color.white;
        lblTitle.style.unityFontStyleAndWeight = FontStyle.Bold;

        var btnClose = new Button(() => _root.Remove(overlay));
        btnClose.text = "ĐÓNG X";
        btnClose.style.backgroundColor = Color.red;
        btnClose.style.color = Color.white;

        header.Add(lblTitle);
        header.Add(btnClose);
        overlay.Add(header);

        // 4. Bố cục chính: Chia đôi (Trái: Danh sách tên | Phải: Danh sách ảnh)
        var body = new VisualElement();
        body.style.flexDirection = FlexDirection.Row;
        body.style.flexGrow = 1; // Chiếm hết chỗ trống còn lại
        body.style.marginTop = 10;

        // --- CỘT TRÁI: DANH SÁCH SINH VIÊN ---
        var leftPanel = new ScrollView();
        leftPanel.style.width = new Length(30, LengthUnit.Percent); // Chiếm 30% bề ngang
        leftPanel.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f);
        leftPanel.style.marginRight = 10;

        // --- CỘT PHẢI: LƯỚI ẢNH ---
        var rightPanel = new ScrollView();
        rightPanel.style.width = new Length(70, LengthUnit.Percent); // Chiếm 70% bề ngang
        rightPanel.style.backgroundColor = new Color(0.3f, 0.3f, 0.3f);
        rightPanel.style.flexDirection = FlexDirection.Row;
        rightPanel.style.flexWrap = Wrap.Wrap; // Tự xuống dòng nếu hết chỗ
        rightPanel.style.paddingLeft = 10; rightPanel.style.paddingTop = 10;

        body.Add(leftPanel);
        body.Add(rightPanel);
        overlay.Add(body);
        _root.Add(overlay);

        // --- HÀM LOAD DỮ LIỆU ---
        // --- HÀM LOAD DANH SÁCH & XỬ LÝ XÓA THƯ MỤC ---
        void RefreshStudentList()
        {
            leftPanel.Clear();
            rightPanel.Clear();

            // Kiểm tra thư mục gốc có tồn tại không
            if (!Directory.Exists(photosRootPath))
            {
                leftPanel.Add(new Label("Chưa có dữ liệu!"));
                return;
            }

            // Lấy danh sách tất cả các thư mục con (mỗi thư mục là 1 sinh viên)
            string[] directories = Directory.GetDirectories(photosRootPath);

            foreach (var dir in directories)
            {
                var dirInfo = new DirectoryInfo(dir);
                string studentName = dirInfo.Name; // Tên folder chính là tên SV

                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.marginBottom = 2;
                row.style.backgroundColor = new Color(0.4f, 0.4f, 0.4f);
                row.style.paddingTop = 5;
                row.style.paddingBottom = 5;
                row.style.paddingLeft = 5;
                row.style.paddingRight = 5;
                row.style.alignItems = Align.Center;

                // 1. Nút bấm vào Tên -> Để xem ảnh
                var btnName = new Button(() =>
                {
                    LoadImagesForStudent(dir, rightPanel, RefreshStudentList);
                });
                btnName.text = studentName;
                btnName.style.flexGrow = 1; // Dãn dài ra
                btnName.style.unityTextAlign = TextAnchor.MiddleLeft;

                // =========================================================
                // 2. NÚT XÓA THƯ MỤC (XÓA CẢ SINH VIÊN) - LOGIC BẠN CẦN
                // =========================================================
                var btnDeleteFolder = new Button(() =>
                {

                    // A. Xóa thư mục trên máy tính (Quan trọng nhất)
                    // Lệnh Directory.Delete(path, true) nghĩa là:
                    // Xóa folder này VÀ toàn bộ file bên trong nó (true = recursive)
                    try
                    {
                        if (Directory.Exists(dir))
                        {
                            Directory.Delete(dir, true);
                            Debug.Log($"[LOCAL] Đã xóa bay màu thư mục: {studentName}");

                            // Load lại danh sách ngay lập tức để dòng này biến mất
                            RefreshStudentList();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("Không xóa được folder máy: " + ex.Message);
                    }

                    // B. Xóa trên Cloud (Nếu SDK cũ bị lỗi thì phần này chỉ hiện cảnh báo)
                    // Vì Firebase không có lệnh "Xóa Folder", ta phải xóa từng file, 
                    // nhưng nếu SDK lỗi thì ta bỏ qua, chỉ cần xóa trên máy là Python đã hiểu rồi.
                    Debug.LogWarning($"[CLOUD] Hãy nhớ xóa folder '{studentName}' trên Web Firebase nếu cần.");

                });

                // Trang trí nút Xóa Folder
                btnDeleteFolder.text = "Xóa SV";
                btnDeleteFolder.style.backgroundColor = new Color(0.8f, 0.2f, 0.2f); // Đỏ đậm
                btnDeleteFolder.style.color = Color.white;
                btnDeleteFolder.style.fontSize = 11;

                row.Add(btnName);
                row.Add(btnDeleteFolder);
                leftPanel.Add(row);
            }
        }

        // Gọi hàm load lần đầu
        RefreshStudentList();
    }

    // --- HÀM LOAD ẢNH CỦA 1 SINH VIÊN ---
    void LoadImagesForStudent(string studentPath, VisualElement container, Action onRefresh)
    {
        container.Clear();

        // Tiêu đề đang xem ai
        var title = new Label($"Đang xem: {Path.GetFileName(studentPath)}");
        title.style.width = new Length(100, LengthUnit.Percent);
        title.style.fontSize = 18;
        title.style.color = Color.yellow;
        title.style.marginBottom = 10;
        container.Add(title);

        string[] images = Directory.GetFiles(studentPath, "*.*"); // Lấy mọi file

        foreach (var imgPath in images)
        {
            // Chỉ lấy file ảnh (jpg, png)
            if (!imgPath.EndsWith(".jpg") && !imgPath.EndsWith(".png") && !imgPath.EndsWith(".jpeg")) continue;

            // Box chứa 1 ảnh
            var imgBox = new VisualElement();
            imgBox.style.width = 100;
            imgBox.style.height = 120;
            imgBox.style.marginRight = 10; imgBox.style.marginBottom = 10;
            imgBox.style.backgroundColor = Color.black;

            // Load Texture
            Texture2D tex = new Texture2D(2, 2);
            byte[] fileData = File.ReadAllBytes(imgPath);
            tex.LoadImage(fileData);

            // Hiển thị ảnh
            var imgDisplay = new VisualElement();
            imgDisplay.style.backgroundImage = new StyleBackground(tex);
            imgDisplay.style.width = 100; imgDisplay.style.height = 100;
            imgDisplay.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);

            // Nút xóa ảnh nhỏ (dấu X)
            var btnDelImg = new Button(() =>
            {
                string photosRoot = Path.Combine(Application.streamingAssetsPath, "FaceAI", "Recognition", "photos");
                string relativePath = imgPath.Replace(photosRoot, "").Replace("\\", "/");
                if (relativePath.StartsWith("/")) relativePath = relativePath.Substring(1);

                // 1. Xóa Local
                try
                {
                    if (File.Exists(imgPath)) File.Delete(imgPath);
                    LoadImagesForStudent(studentPath, container, onRefresh);
                }
                catch (Exception ex) { Debug.LogError("Lỗi xóa file: " + ex.Message); }

                // 2. Xóa Cloud (Dùng DeleteAsync đơn giản)
                FirebaseStorage storage = FirebaseStorage.DefaultInstance;
                StorageReference fileRef = storage.GetReferenceFromUrl(storageBucketLink).Child(relativePath);

                // Dùng ContinueWithOnMainThread để xử lý kết quả an toàn hơn
                fileRef.DeleteAsync().ContinueWithOnMainThread(task =>
                {
                    if (task.IsFaulted)
                    {
                        Debug.LogError($"Lỗi xóa Cloud: {task.Exception}");
                        ShowToast("❌ Lỗi xóa ảnh trên Cloud!", colRed);
                    }
                    else if (task.IsCompleted)
                    {
                        ShowToast("🗑 Đã xóa ảnh trên Cloud!", colGreen);
                    }
                });
            });
            btnDelImg.text = "X";
            btnDelImg.style.position = Position.Absolute;
            btnDelImg.style.right = 0; btnDelImg.style.top = 0;
            btnDelImg.style.backgroundColor = Color.red;
            btnDelImg.style.color = Color.white;
            btnDelImg.style.width = 20; btnDelImg.style.height = 20;

            imgBox.Add(imgDisplay);
            imgBox.Add(btnDelImg);
            container.Add(imgBox);
        }

        if (images.Length == 0)
        {
            container.Add(new Label("Thư mục trống!"));
        }
    }

    // --- NEW: Helper lấy dữ liệu Face + Landmarks để tính Yaw ---
    // --- NEW: Helper lấy dữ liệu Face + Landmarks để tính Yaw ---
    // Added texWidth/texHeight to normalize pixel coordinates if needed
    private bool TryGetFaceDataFromLastResult(out Rect rectNorm, out float yaw, out bool hasFace, int texWidth = 640, int texHeight = 480)
    {
        rectNorm = default;
        yaw = 0f;
        hasFace = false;

        if (faceDetector == null || !faceDetector.HasLastResult) return false;

        var r = faceDetector.LastResult;
        var rt = r.GetType();

        // 1. Lấy List Detections
        object detListObj = rt.GetProperty("detections")?.GetValue(r) ?? rt.GetProperty("Detections")?.GetValue(r)
            ?? rt.GetField("detections")?.GetValue(r) ?? rt.GetField("Detections")?.GetValue(r);

        if (detListObj is not System.Collections.IList detList || detList.Count == 0) return false;

        var det = detList[0];
        if (det == null) return false;

        hasFace = true;

        // 2. Lấy Bounding Box
        // Priority: LocationData -> RelativeBoundingBox (Normalized)
        // Fallback: BoundingBox (Pixels)

        var dt = det.GetType();
        object boxObj = null;
        bool isAlreadyNormalized = false;

        // Check for LocationData first (Common in MP Unity)
        object locData = dt.GetProperty("LocationData")?.GetValue(det) ?? dt.GetProperty("locationData")?.GetValue(det);
        if (locData != null)
        {
            var ldt = locData.GetType();
            boxObj = ldt.GetProperty("RelativeBoundingBox")?.GetValue(locData) ?? ldt.GetProperty("relativeBoundingBox")?.GetValue(locData);
            if (boxObj != null) isAlreadyNormalized = true;
            else
            {
                boxObj = ldt.GetProperty("BoundingBox")?.GetValue(locData) ?? ldt.GetProperty("boundingBox")?.GetValue(locData);
            }
        }

        // Fallback direct properties on Detection
        if (boxObj == null)
        {
            boxObj = dt.GetProperty("RelativeBoundingBox")?.GetValue(det) ?? dt.GetProperty("relativeBoundingBox")?.GetValue(det);
            if (boxObj != null) isAlreadyNormalized = true;
        }

        if (boxObj == null)
        {
            boxObj = dt.GetProperty("BoundingBox")?.GetValue(det) ?? dt.GetProperty("boundingBox")?.GetValue(det)
                 ?? dt.GetProperty("Rect")?.GetValue(det) ?? dt.GetProperty("rect")?.GetValue(det);
        }

        if (boxObj != null)
        {
            // Fix: ReadFloat signature mismatch (removed Type arg)
            float xMin = ReadFloat(boxObj, "XMin", "xMin", "xmin", "X", "x") ?? 0;
            float yMin = ReadFloat(boxObj, "YMin", "yMin", "ymin", "Y", "y") ?? 0;
            float w = ReadFloat(boxObj, "Width", "width", "W", "w") ?? 0;
            float h = ReadFloat(boxObj, "Height", "height", "H", "h") ?? 0;

            if (!isAlreadyNormalized)
            {
                // Verify if values seem large (Pixels)
                if (xMin > 1.0f || w > 1.0f)
                {
                    xMin /= texWidth;
                    yMin /= texHeight;
                    w /= texWidth;
                    h /= texHeight;
                }
            }

            rectNorm = new Rect(xMin, yMin, w, h);
        }

        // 3. Tính YAW (Góc quay đầu) từ Landmarks (Eyes, Nose, Ears)
        // Keypoints index: 0=LeftEye, 1=RightEye, 2=NoseTip, 3=MouthCenter, 4=LeftEar, 5=RightEar
        object kpListObj = dt.GetProperty("Keypoints")?.GetValue(det) ?? dt.GetProperty("keypoints")?.GetValue(det)
             ?? dt.GetField("Keypoints")?.GetValue(det) ?? dt.GetField("keypoints")?.GetValue(det);

        if (kpListObj is System.Collections.IList kpList && kpList.Count >= 6)
        {
            // Helper lấy X coordinate của keypoint
            float GetX(int index)
            {
                try
                {
                    if (kpList == null || index < 0 || index >= kpList.Count) return 0; // [FIX] Bounds check
                    var kp = kpList[index];
                    if (kp == null) return 0;
                    return ReadFloat(kp, "X", "x") ?? 0;
                }
                catch { return 0; }
            }

            float noseX = GetX(2);
            float leftEarX = GetX(4);
            float rightEarX = GetX(5);

            // Logic đơn giản:
            // Nhìn thẳng: Mũi nằm giữa 2 tai -> Dist(Mũi, TaiTrái) ~ Dist(Mũi, TaiPhải)
            // Quay Trái (của user, tức bên Phải màn hình): Mũi gần Tai Trái (index 4) hơn
            // Quay Phải (của user): Mũi gần Tai Phải (index 5) hơn

            float distL = Mathf.Abs(noseX - leftEarX); // Mũi -> Tai Trái
            float distR = Mathf.Abs(noseX - rightEarX); // Mũi -> Tai Phải

            // Ratio: Nếu nhìn thẳng ratio ~ 1.
            // < 1: Lệch trái? > 1: Lệch phải?
            // Để dễ debug, ta dùng hiệu số chuẩn hóa hoặc góc giả lập

            // Cách tính Yaw ước lượng (Heuristic):
            // (distL - distR) / (distL + distR) -> Range -1..1
            // 0: Thẳng, < 0: Quay phải, > 0: Quay trái (Tùy hệ tọa độ MediaPipe)

            float ratio = (distL - distR) / (distL + distR + 0.0001f);

            // Map ratio (-0.5 .. 0.5) sang độ (-45 .. 45)
            yaw = ratio * 90f;
        }

        return true;
    }

    private bool TryGetFaceRectNormFromLastResult(out Rect rectNorm)
    {
        rectNorm = default;

        if (faceDetector == null || !faceDetector.HasLastResult) return false;

        var r = faceDetector.LastResult;

        // Lấy list detections: property/field thường là "detections" hoặc "Detections"
        var rt = r.GetType();
        object detListObj =
            rt.GetProperty("detections")?.GetValue(r) ??
            rt.GetProperty("Detections")?.GetValue(r) ??
            rt.GetField("detections")?.GetValue(r) ??
            rt.GetField("Detections")?.GetValue(r);

        if (detListObj is not System.Collections.IList detList || detList.Count == 0)
            return false;

        var det = detList[0];
        if (det == null) return false;

        // Tìm object “rect/box” bên trong Detection (tên khác nhau theo version)
        // Ta thử các tên phổ biến trước:
        var dt = det.GetType();
        object boxObj =
            dt.GetProperty("BoundingBox")?.GetValue(det) ??
            dt.GetProperty("boundingBox")?.GetValue(det) ??
            dt.GetProperty("Rect")?.GetValue(det) ??
            dt.GetProperty("rect")?.GetValue(det) ??
            dt.GetProperty("LocationData")?.GetValue(det) ??
            dt.GetProperty("locationData")?.GetValue(det) ??
            dt.GetField("BoundingBox")?.GetValue(det) ??
            dt.GetField("boundingBox")?.GetValue(det) ??
            dt.GetField("Rect")?.GetValue(det) ??
            dt.GetField("rect")?.GetValue(det) ??
            dt.GetField("LocationData")?.GetValue(det) ??
            dt.GetField("locationData")?.GetValue(det);

        if (boxObj == null) return false;

        // Lấy (x,y,w,h) theo các kiểu field phổ biến
        // Ưu tiên pixel-based: OriginX/OriginY/Width/Height
        float? ox = ReadFloat(boxObj, "OriginX", "origin_x", "XMin", "xmin", "Left");
        float? oy = ReadFloat(boxObj, "OriginY", "origin_y", "YMin", "ymin", "Top");
        float? w = ReadFloat(boxObj, "Width", "width");
        float? h = ReadFloat(boxObj, "Height", "height");

        // Nếu có center-based
        float? cx = ReadFloat(boxObj, "XCenter", "x_center");
        float? cy = ReadFloat(boxObj, "YCenter", "y_center");

        var t = ImageSourceProvider.ImageSource?.GetCurrentTexture();
        if (t == null) return false;
        int imgW = t.width;
        int imgH = t.height;
        if (imgW <= 0 || imgH <= 0) return false;

        float x, y, ww, hh;

        if (ox.HasValue && oy.HasValue && w.HasValue && h.HasValue)
        {
            // pixel → norm
            x = ox.Value / imgW;
            y = oy.Value / imgH;
            ww = w.Value / imgW;
            hh = h.Value / imgH;
        }
        else if (cx.HasValue && cy.HasValue && w.HasValue && h.HasValue)
        {
            // center pixel → norm
            x = (cx.Value - w.Value / 2f) / imgW;
            y = (cy.Value - h.Value / 2f) / imgH;
            ww = w.Value / imgW;
            hh = h.Value / imgH;
        }
        else
        {
            return false;
        }

        rectNorm = new Rect(Mathf.Clamp01(x), Mathf.Clamp01(y), Mathf.Clamp01(ww), Mathf.Clamp01(hh));
        return rectNorm.width > 0 && rectNorm.height > 0;
    }

    private float? ReadFloat(object obj, params string[] names)
    {
        var t = obj.GetType();
        foreach (var n in names)
        {
            var p = t.GetProperty(n);
            if (p != null && p.PropertyType == typeof(float)) return (float)p.GetValue(obj);
            if (p != null && p.PropertyType == typeof(int)) return (int)p.GetValue(obj);

            var f = t.GetField(n);
            if (f != null && f.FieldType == typeof(float)) return (float)f.GetValue(obj);
            if (f != null && f.FieldType == typeof(int)) return (int)f.GetValue(obj);
        }
        return null;
    }

    public void SendImageForTraining(string name, string mssv, Texture2D photo = null)
    {
        Debug.Log($"[SendImageForTraining] Start. faceDetector exists? {faceDetector != null}");

        // FIX #2: Gửi full frame không cần crop bbox client
        // Nếu đã có ảnh chụp sẵn (từ bước Training) thì dùng luôn
        Texture2D src = photo;
        if (src == null)
        {
            src = CaptureFromMediaPipe();
        }

        if (src == null)
        {
            Debug.LogWarning("❌ No MediaPipe frame captured!");
            return;
        }

        // Encode full frame
        byte[] jpg = src.EncodeToJPG(90);
        string b64 = Convert.ToBase64String(jpg);

        Debug.Log($"📸 Captured Full Frame ({src.width}x{src.height}). Sending to Register API...");
        StartCoroutine(PostRegister(b64, name, mssv));
    }
    [Serializable]
    private class RegisterReq
    {
        public string img_base64;
        public string name;
        public string mssv;
    }

    private IEnumerator PostRegister(string imgBase64, string name, string mssv, bool reloadIndex = true)
    {
        string url = cloudRunApiUrl.TrimEnd('/') + "/register";

        var reqObj = new RegisterReq
        {
            img_base64 = imgBase64,
            name = name,
            mssv = mssv
        };

        string json = JsonUtility.ToJson(reqObj);
        byte[] body = System.Text.Encoding.UTF8.GetBytes(json);

        using UnityEngine.Networking.UnityWebRequest req =
            new UnityEngine.Networking.UnityWebRequest(url, "POST");

        req.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(body);
        req.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        ApplyTenantHeaders(req);
        req.timeout = 120; // Increased for RetinaFace (slower but more accurate)

        yield return req.SendWebRequest();

        if (req.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
        {
            string errResponse = req.downloadHandler.text;
            Debug.LogError("Register failed: " + req.error + " | " + errResponse);

            // Try to extract clean message
            string cleanMsg = req.error;
            if (errResponse.Contains("No face detected")) cleanMsg = "Không tìm thấy khuôn mặt trong ảnh!";
            else if (errResponse.Contains("message")) cleanMsg = errResponse;

            _lastRegisterOk = false;
            _lastRegisterErr = cleanMsg;

            yield break; // ✅ không throw nữa - tránh crash
        }
        else
        {
            _lastRegisterOk = true;
            _lastRegisterErr = "";
            Debug.Log("REGISTER OK: " + req.downloadHandler.text);

            // Sau khi đăng ký thành công -> Reload FAISS (if requested)
            if (reloadIndex)
            {
                ReloadFaissIndex();
            }
        }
    }

    // --- THAY THẾ HÀM CaptureFromMediaPipe CŨ BẰNG HÀM NÀY ---
    // --- [NEW] CAPTURE & CROP LOGIC (A) ---

    // Hàm này thay thế hàm cũ, trả về ảnh CROP hoặc FULL tùy setting
    private Texture2D CaptureFromMediaPipe()
    {
        // 1. Lấy ảnh gốc (Full Frame)
        Texture2D fullTex = CaptureFullFrameTexture2D(); // Hàm cũ đổi tên
        if (fullTex == null) return null;

        // 2. Nếu ko crop -> trả full
        if (!sendCroppedFace) return fullTex;

        // 3. Tính Crop Rect
        // Dùng bbox mới nhất
        if (TryGetCropRectPx(fullTex, _lastFaceRectNorm, cropMargin, out RectInt cropRect))
        {
            // Debug.Log($"[CROP] Rect: {cropRect}");
            // 4. Crop
            Texture2D cropped = CropTexture(fullTex, cropRect);

            // Cleanup fullTex vì ta chỉ cần cropped
            Destroy(fullTex);
            return cropped;
        }

        // Nếu tính crop fail (bbox quá nhỏ/ko hợp lệ) -> Trả full (fallback) hoặc null?
        // Ở đây ta fallback trả full
        return fullTex;
    }

    // Hàm cũ (được đổi tên) để lấy Raw Texture từ màn hình/Camera
    private Texture2D CaptureFullFrameTexture2D()
    {
        var imageSource = ImageSourceProvider.ImageSource;
        if (imageSource == null) return null;

        // [FIX] Wait until camera is ready before capturing
        if (!imageSource.isPrepared || !imageSource.isPlaying) return null;

        Texture srcTex = imageSource.GetCurrentTexture();
        if (srcTex == null) return null;

        // 1. Tạo RenderTexture tạm
        RenderTexture tempRT = RenderTexture.GetTemporary(srcTex.width, srcTex.height, 0, RenderTextureFormat.ARGB32);

        // 2. KỸ THUẬT LẬT ẢNH BẰNG GPU (An toàn tuyệt đối)
        // Scale (-1, 1) nghĩa là lật ngược trục X (Ngang)
        // Offset (1, 0) để đưa hình về lại khung nhìn sau khi lật
        Graphics.Blit(srcTex, tempRT, new Vector2(-1, 1), new Vector2(1, 0));

        // 3. Đọc dữ liệu
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = tempRT;

        Texture2D fullTex = new Texture2D(srcTex.width, srcTex.height, TextureFormat.RGB24, false);
        fullTex.ReadPixels(new Rect(0, 0, srcTex.width, srcTex.height), 0, 0);
        fullTex.Apply();

        // 4. Dọn dẹp
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(tempRT);

        return fullTex;
    }

    // Helper: Tính toán vùng Crop pixel từ Normalized Rect + Margin
    private bool TryGetCropRectPx(Texture2D fullTex, Rect faceRectNorm, float margin, out RectInt cropPx)
    {
        cropPx = new RectInt(0, 0, 0, 0);
        if (faceRectNorm.width <= 0 || faceRectNorm.height <= 0) return false;

        int wTex = fullTex.width;
        int hTex = fullTex.height;

        // 1. Apply Margin (Normalized)
        float cx = faceRectNorm.x + faceRectNorm.width / 2f;
        float cy = faceRectNorm.y + faceRectNorm.height / 2f;

        // Mở rộng width/height
        float expandW = faceRectNorm.width * (1f + margin);
        float expandH = faceRectNorm.height * (1f + margin);

        // 2. Tính toán xMin, yMin (Norm)
        float xMin = cx - expandW / 2f;
        float yMin = cy - expandH / 2f; // MediaPipe gốc thường là Top-Left (0,0) xuống

        // [QUAN TRỌNG] Xử lý Mirror X (vì CaptureFullFrameTexture2D đã Flip X rồi!)
        // Nếu Bbox gốc từ MediaPipe là (x_norm), thì sau khi lật ảnh: x_new = 1.0 - (x_norm + w_norm)
        // Tuy nhiên `Graphics.Blit` lật toàn bộ ảnh, nhưng `_lastFaceRectNorm` đang là toạ độ RAW từ detection (chưa lật).
        // Nếu logic vẽ Box UI của bạn khớp, thì RectNorm này KHỚP với ảnh HIỂN THỊ (đã lật).
        // Nhưng nếu RectNorm lấy thẳng từ MP (chưa lật) và ảnh Capture cũng chưa lật... 
        // -> Hồi nãy `CaptureFullFrameTexture2D` CÓ LẬT (`Vector2(-1, 1)`).
        // -> MediaPipe ra toạ độ "Thật" (ví dụ user nghiêng trái -> detect trái).
        // -> Khi mirror ảnh, pixel trái chạy sang phải.
        // -> Ta cần mirror rect: newX = 1 - (oldX + oldW).

        if (mirrorXWhenCropping)
        {
            float xMax = xMin + expandW;
            // Flip trục X norm:
            // xMin' = 1 - xMax
            xMin = 1f - (xMin + expandW);
        }

        // 3. Convert to Pixel
        // Unity Texture2D: (0,0) is Bottom-Left. MediaPipe usually Top-Left.
        // MediaPipe Y=0 is Top.
        // Texture2D Y=0 is Bottom.
        // => PixelY = (1 - (yMin + expandH)) * hTex ?
        // Hãy test logic: yMinNorm là Top. yMaxNorm là Bottom.
        // Unity yMinPixel = (1 - yMaxNorm) * hTex.

        float yMax = yMin + expandH;
        int pX = Mathf.FloorToInt(Mathf.Clamp01(xMin) * wTex);
        int pY = Mathf.FloorToInt(Mathf.Clamp01(1f - yMax) * hTex); // Đảo Y cho Unity Tex
        int pW = Mathf.FloorToInt(MatchWidth(xMin, expandW) * wTex);
        int pH = Mathf.FloorToInt(MatchHeight(yMin, expandH) * hTex);

        // Hàm phụ clamp
        float MatchWidth(float x, float w)
        {
            float left = Mathf.Max(0, x);
            float right = Mathf.Min(1, x + w);
            return Mathf.Max(0, right - left);
        }
        float MatchHeight(float y, float h)
        { // Y này vẫn là logic Top-Down
            float top = Mathf.Max(0, y);
            float bot = Mathf.Min(1, y + h);
            return Mathf.Max(0, bot - top);
        }

        // Re-calc pW, pH trực tiếp từ Rect
        pW = Mathf.FloorToInt(expandW * wTex);
        pH = Mathf.FloorToInt(expandH * hTex);

        // Clamp thủ công lần cuối cho chắc
        pX = Mathf.Clamp(pX, 0, wTex - 1);
        pY = Mathf.Clamp(pY, 0, hTex - 1);
        pW = Mathf.Clamp(pW, 1, wTex - pX);
        pH = Mathf.Clamp(pH, 1, hTex - pY);

        if (pW < cropMinSizePx || pH < cropMinSizePx) return false;

        cropPx = new RectInt(pX, pY, pW, pH);
        return true;
    }

    private Texture2D CropTexture(Texture2D original, RectInt region)
    {
        Texture2D result = new Texture2D(region.width, region.height, TextureFormat.RGB24, false);
        // GetPixels(x, y, w, h)
        // Unity Texture coordinates: (0,0) is bottom-left.
        // region.y should be from bottom.

        Color[] pixels = original.GetPixels(region.x, region.y, region.width, region.height);
        result.SetPixels(pixels);
        result.Apply();
        return result;
    }

    // --- P0: LOCAL PERSISTENCE HELPERS ---
    [System.Serializable]
    public class LogListWrapper
    {
        public List<AttendanceLog> logs;
    }

    private void SaveLogsLocal()
    {
        try
        {
            if (_allLogs == null) return;
            string path = LogsLocalPath; // Use dynamic path per class/term
            var wrapper = new LogListWrapper { logs = _allLogs };
            string json = JsonUtility.ToJson(wrapper, true);
            System.IO.File.WriteAllText(path, json);
            // Debug.Log($"✅ Saved local logs to {path}");
        }
        catch (System.Exception ex)
        {
            Debug.LogError("SaveLogsLocal Failed: " + ex.Message);
        }
    }

    private void LoadLogsLocal()
    {
        try
        {
            string path = LogsLocalPath; // Use dynamic path per class/term
            if (!System.IO.File.Exists(path))
            {
                _allLogs = new List<AttendanceLog>();
                return;
            }

            string json = System.IO.File.ReadAllText(path);
            var wrapper = JsonUtility.FromJson<LogListWrapper>(json);
            if (wrapper != null && wrapper.logs != null)
            {
                _allLogs = wrapper.logs;
                Debug.Log($"✅ Loaded {_allLogs.Count} logs from {path}");
            }
            else
            {
                _allLogs = new List<AttendanceLog>();
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError("LoadLogsLocal Failed: " + ex.Message);
        }
    }

    // [NEW] Hidden Logs Helpers
    private void LoadHiddenLogIds()
    {
        try
        {
            if (!System.IO.File.Exists(HiddenLogLocalPath))
            {
                _hiddenLogIds = new HashSet<string>();
                return;
            }

            var json = System.IO.File.ReadAllText(HiddenLogLocalPath);
            if (string.IsNullOrEmpty(json))
            {
                _hiddenLogIds = new HashSet<string>();
                return;
            }

            var wrapper = JsonUtility.FromJson<HiddenLogIdsWrapper>(json);
            _hiddenLogIds = new HashSet<string>(wrapper?.ids ?? System.Array.Empty<string>());
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[HiddenLog] Load failed: " + e.Message);
            _hiddenLogIds = new HashSet<string>();
        }
    }

    private void SaveHiddenLogIds()
    {
        try
        {
            var wrapper = new HiddenLogIdsWrapper { ids = System.Linq.Enumerable.ToArray(_hiddenLogIds) };
            var json = JsonUtility.ToJson(wrapper, false);
            System.IO.File.WriteAllText(HiddenLogLocalPath, json);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[HiddenLog] Save failed: " + e.Message);
        }
    }

    // =================== MULTI-CLASS (Student Detail) HELPERS ===================
    // Chỉ dùng để đọc/đồng bộ logs cho *lớp khác* mà không cần SwitchClass()
    private readonly HashSet<string> _multiClassLogSyncRequested = new HashSet<string>();

    private string GetLogsLocalPathFor(string termId, string classCode)
    {
        string t = string.IsNullOrEmpty(termId) ? "legacy" : termId;
        string key = $"{SafeFileToken(t)}__{SafeFileToken(classCode)}";
        return Path.Combine(Application.persistentDataPath, $"attendance_logs_{SafeTeacherToken}_{key}.json");
    }

    private List<AttendanceLog> LoadLogsLocalFor(string termId, string classCode)
    {
        try
        {
            string path = GetLogsLocalPathFor(termId, classCode);
            if (!File.Exists(path)) return new List<AttendanceLog>();

            string json = File.ReadAllText(path);
            var wrapper = JsonUtility.FromJson<LogListWrapper>(json);
            return wrapper?.logs ?? new List<AttendanceLog>();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MultiClass] LoadLogsLocalFor failed: {e.Message}");
            return new List<AttendanceLog>();
        }
    }

    private void SaveLogsLocalFor(string termId, string classCode, List<AttendanceLog> logs)
    {
        try
        {
            logs ??= new List<AttendanceLog>();
            string path = GetLogsLocalPathFor(termId, classCode);
            var wrapper = new LogListWrapper { logs = logs };
            File.WriteAllText(path, JsonUtility.ToJson(wrapper, true));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MultiClass] SaveLogsLocalFor failed: {e.Message}");
        }
    }

    // Overload: apply tenant headers for arbitrary class (Teacher giữ nguyên)
    private void ApplyTenantHeadersFor(UnityWebRequest req, string termId, string classCode)
    {
        if (req == null) return;

        if (!string.IsNullOrEmpty(_currentTeacherId))
            req.SetRequestHeader("X-Teacher-ID", _currentTeacherId);

        req.SetRequestHeader("X-Term-ID", string.IsNullOrEmpty(termId) ? "legacy" : termId);

        if (!string.IsNullOrEmpty(classCode))
            req.SetRequestHeader("X-Class-ID", classCode);
    }

    private IEnumerator FetchLogsFromServerFor(string termId, string classCode, Action<List<AttendanceLog>> onDone, bool silent = true)
    {
        string url = cloudRunApiUrl.TrimEnd('/') + $"/logs?limit={LOG_FETCH_LIMIT}";

        using (var req = UnityWebRequest.Get(url))
        {
            ApplyTenantHeadersFor(req, termId, classCode);
            req.timeout = 120;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[MultiClass] GET /logs failed for class '{classCode}': {req.error} | {req.downloadHandler.text}");
                onDone?.Invoke(new List<AttendanceLog>());
                yield break;
            }

            var resp = JsonUtility.FromJson<LogsResponse>(req.downloadHandler.text);
            var list = new List<AttendanceLog>();

            if (resp != null && resp.logs != null)
            {
                foreach (var it in resp.logs)
                {
                    if (it == null || string.IsNullOrEmpty(it.id)) continue;

                    string t = string.IsNullOrEmpty(it.time) ? DateTime.Now.ToString("HH:mm:ss") : it.time;
                    long ts = it.vn_epoch_ms;
                    string d = it.date;

                    if (ts <= 0)
                    {
                        if (!string.IsNullOrEmpty(it.date) && !string.IsNullOrEmpty(it.time))
                        {
                            try
                            {
                                DateTime dt = DateTime.ParseExact($"{it.date} {it.time}", "dd/MM/yyyy HH:mm:ss",
                                    System.Globalization.CultureInfo.InvariantCulture);
                                ts = ((DateTimeOffset)dt).ToUnixTimeMilliseconds();
                            }
                            catch { }
                        }
                        if (ts <= 0) ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    }

                    if (string.IsNullOrEmpty(d))
                    {
                        d = DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime().ToString("dd/MM/yyyy");
                    }
                    else if (d.Contains("-") && d.Length == 10)
                    {
                        if (DateTime.TryParseExact(d, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out DateTime dtServer))
                            d = dtServer.ToString("dd/MM/yyyy");
                    }

                    list.Add(new AttendanceLog
                    {
                        id = it.id,
                        studentName = it.name,
                        mssv = it.mssv,
                        time = t,
                        date = d,
                        status = ComputeStatusByTime(t, it.status),
                        confidence = it.match_percent,
                        timestamp = EpochMsToTicks(ts),
                        isSynced = true
                    });
                }
            }

            list = list.OrderByDescending(x => x.timestamp).ToList();
            SaveLogsLocalFor(termId, classCode, list);
            onDone?.Invoke(list);
        }
    }

    // Schedule helpers for arbitrary classCode (không phụ thuộc _currentClassCode)
    private bool HasClassFor(DateTime date, string classCode)
    {
        if (_scheduleList == null || _scheduleList.Count == 0) return false;

        int dow = (int)date.DayOfWeek;
        int scheduleDow = (dow == 0) ? 8 : (dow + 1);

        string cc = (classCode ?? "").Trim();
        return _scheduleList.Any(s =>
            s.dayOfWeek == scheduleDow &&
            (s.classCode ?? "").Trim().Equals(cc, StringComparison.OrdinalIgnoreCase)
        );
    }

    private bool HasClassFinishedFor(DateTime date, string classCode)
    {
        if (!HasClassFor(date, classCode)) return false;

        if (date.Date < DateTime.Today) return true;
        if (date.Date > DateTime.Today) return false;

        int dow = (int)date.DayOfWeek;
        int scheduleDow = (dow == 0) ? 8 : (dow + 1);

        string cc = (classCode ?? "").Trim();
        var sessionsToday = _scheduleList
            .Where(s => s.dayOfWeek == scheduleDow && (s.classCode ?? "").Trim().Equals(cc, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sessionsToday.Count == 0) return false;

        DateTime latestEnd = DateTime.MinValue;
        foreach (var s in sessionsToday)
        {
            int endP = s.endPeriod > 0 ? s.endPeriod : s.startPeriod;
            var pt = GetPeriodTime(endP);
            DateTime endTime = new DateTime(date.Year, date.Month, date.Day, pt.endHour, pt.endMin, 0);
            if (endTime > latestEnd) latestEnd = endTime;
        }

        return DateTime.Now >= latestEnd;
    }

    // Helper to check if status is absent
    private bool IsAbsentStatusHelper(string s)
    {
        s = (s ?? "").Trim().ToLowerInvariant();
        return s == "absent" || s == "vắng" || s == "vang";
    }


    private void HideLogLocal(string logId)
    {
        if (string.IsNullOrEmpty(logId)) return;

        _hiddenLogIds.Add(logId);
        SaveHiddenLogIds();

        // Xóa khỏi list hiện tại
        _allLogs.RemoveAll(x => x.id == logId);

        // Cập nhật UI
        UpdateTopStats();
        RefreshMainContent();
    }

    // [NEW] API Delete Profile
    IEnumerator DeleteProfile(string profileKey, bool deleteRoster, Action<bool> onDone = null)
    {
        string apiUrl = cloudRunApiUrl.TrimEnd('/') + "/delete_profile";
        // Manual JSON construction to avoid dependency on struct placement if relevant
        string payload = $"{{\"profile_key\":\"{profileKey}\", \"delete_roster\":{(deleteRoster ? "true" : "false")}}}";
        byte[] body = System.Text.Encoding.UTF8.GetBytes(payload);

        using (var req = new UnityWebRequest(apiUrl, "POST"))
        {
            req.uploadHandler = (UploadHandler)new UploadHandlerRaw(body);
            req.downloadHandler = (DownloadHandler)new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            ApplyTenantHeaders(req); // [FIX] Add tenant headers for multi-tenancy

            yield return req.SendWebRequest();

            bool success = req.result == UnityWebRequest.Result.Success;
            if (!success)
                Debug.LogError("DeleteProfile fail: " + req.error + " | " + req.downloadHandler.text);
            else
                Debug.Log("DeleteProfile ok: " + req.downloadHandler.text);

            onDone?.Invoke(success);
        }
    }



    // [NEW] Manager UI & API
    // =========================================================

    private void ShowFaceDataManager()
    {
        // Overlay Setup
        var overlay = new VisualElement();
        overlay.style.position = Position.Absolute;
        overlay.style.top = 0; overlay.style.bottom = 0;
        overlay.style.left = 0; overlay.style.right = 0;
        overlay.style.backgroundColor = new Color(0, 0, 0, 0.85f);
        overlay.style.justifyContent = Justify.Center;
        overlay.style.alignItems = Align.Center;
        overlay.RegisterCallback<ClickEvent>(e => { if (e.target == overlay) _root.Remove(overlay); });

        var card = new VisualElement();
        card.style.width = 500;
        card.style.height = 600;
        card.style.backgroundColor = Color.white;
        card.style.borderTopLeftRadius = 12; card.style.borderTopRightRadius = 12;
        card.style.borderBottomLeftRadius = 12; card.style.borderBottomRightRadius = 12;
        card.style.paddingLeft = 20; card.style.paddingRight = 20;
        card.style.paddingTop = 20; card.style.paddingBottom = 20;
        overlay.Add(card);

        // Header
        var header = new VisualElement { style = { flexDirection = FlexDirection.Row, justifyContent = Justify.SpaceBetween, marginBottom = 15 } };
        card.Add(header);

        var title = new Label("QUẢN LÝ DỮ LIỆU KHUÔN MẶT") { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 20, color = colTextDark } };
        header.Add(title);

        var closeBtn = new Button { text = "✕", style = { fontSize = 18, color = colTextLight, backgroundColor = Color.clear } };
        SetBorderWidth(closeBtn, 0); // [FIX] Explicit border width
        closeBtn.clicked += () => _root.Remove(overlay);
        header.Add(closeBtn);

        // List Container (Scroll)
        var scroll = new ScrollView(ScrollViewMode.Vertical);
        scroll.style.flexGrow = 1;
        card.Add(scroll);

        // Loading Indicator
        var loadingLbl = new Label("Đang tải danh sách...") { style = { alignSelf = Align.Center, marginTop = 50, color = colTextLight } };
        scroll.Add(loadingLbl);

        // Fetch Data
        StartCoroutine(GetProfiles((profiles) =>
        {
            scroll.Remove(loadingLbl);
            if (profiles == null || profiles.Length == 0)
            {
                scroll.Add(new Label("Chưa có dữ liệu nào.") { style = { alignSelf = Align.Center, marginTop = 20, color = colTextLight } });
                return;
            }

            foreach (var p in profiles)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.justifyContent = Justify.SpaceBetween;
                row.style.alignItems = Align.Center;
                row.style.borderBottomWidth = 1;
                row.style.borderBottomColor = new Color(0.9f, 0.9f, 0.9f);
                row.style.paddingTop = 10; row.style.paddingBottom = 10;

                var info = new VisualElement();
                info.Add(new Label(p.name) { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 16, color = colTextDark } });
                info.Add(new Label($"{p.mssv} • {p.doc_count} vectors") { style = { fontSize = 13, color = colTextLight } });
                row.Add(info);

                var delBtn = new Button { text = "Xóa hồ sơ" };
                delBtn.style.backgroundColor = new Color(1f, 0.9f, 0.9f);
                delBtn.style.color = colRed;
                SetBorderWidth(delBtn, 0); // [FIX] Explicit
                delBtn.style.fontSize = 12;
                delBtn.style.paddingTop = 6; delBtn.style.paddingBottom = 6;
                delBtn.style.borderTopLeftRadius = 6; delBtn.style.borderTopRightRadius = 6;
                delBtn.style.borderBottomLeftRadius = 6; delBtn.style.borderBottomRightRadius = 6;

                delBtn.clicked += () =>
                {
                    // Confirm Delete (Basic Toggle Text)
                    if (delBtn.text != "Chắc chắn?")
                    {
                        delBtn.text = "Chắc chắn?";
                        delBtn.schedule.Execute(() => { if (delBtn.text == "Chắc chắn?") delBtn.text = "Xóa hồ sơ"; }).StartingIn(3000);
                        return;
                    }

                    delBtn.text = "Đang xóa...";
                    delBtn.SetEnabled(false);

                    StartCoroutine(DeleteProfile(p.profile_key, (ok) =>
                    {
                        if (ok)
                        {
                            row.style.display = DisplayStyle.None; // Hide Row
                            ShowToast("Đã xóa hồ sơ: " + p.name, colRed);
                        }
                        else
                        {
                            delBtn.text = "Lỗi!";
                            delBtn.SetEnabled(true);
                            ShowToast("Xóa thất bại!", colRed);
                        }
                    }));
                };
                row.Add(delBtn);

                scroll.Add(row);
            }
        }));

        _root.Add(overlay);
    }

    // API: Fetch Profiles
    private IEnumerator GetProfiles(System.Action<FaceProfile[]> onDone)
    {
        // [FIX] Add timestamp to prevent caching
        string url = cloudRunApiUrl.TrimEnd('/') + "/profiles?t=" + System.DateTime.Now.Ticks;
        Debug.Log($"[GetProfiles] Requesting: {url} | TeacherID: {_currentTeacherId}");

        // [FIX] Use constructor to ensure DownloadHandler is attached even for GET
        var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Accept", "application/json"); // Explicitly accept JSON
        ApplyTenantHeaders(req);
        req.timeout = 120; 

        using (req)
        {
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("GET /profiles failed: " + req.error);
                Debug.LogError("URL=" + req.url);
                Debug.LogError("Status=" + req.responseCode + " err=" + req.error);
                Debug.LogError("Body=" + req.downloadHandler.text);
                
                onDone?.Invoke(System.Array.Empty<FaceProfile>());
                yield break;
            }

            var json = req.downloadHandler.text;
            Debug.Log($"[GetProfiles] Response: {json}"); // [DEBUG]

            // Use wrapper because root is object { profiles: [], count: 0 }
            var resp = JsonUtility.FromJson<ProfilesResponse>(json);
            onDone?.Invoke(resp?.profiles ?? System.Array.Empty<FaceProfile>());
        }
    }

    // API: Delete Profile (Overload with Callback)
    private IEnumerator DeleteProfile(string profileKey, System.Action<bool> onDone)
    {
        Debug.Log($"[DeleteProfile] Deleting: {profileKey}");

        string url = cloudRunApiUrl.TrimEnd('/') + "/delete_profile";

        // [FIX v4] Must send JSON body, server uses request.json
        string jsonBody = "{\"profile_key\":\"" + profileKey + "\"}";
        byte[] bodyBytes = System.Text.Encoding.UTF8.GetBytes(jsonBody);

        Debug.Log($"[DeleteProfile] JSON: {jsonBody}");

        var req = new UnityWebRequest(url, "POST");
        req.uploadHandler = new UploadHandlerRaw(bodyBytes);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        ApplyTenantHeaders(req);
        req.timeout = 120; // Increased for RetinaFace

        yield return req.SendWebRequest();

        Debug.Log($"[DeleteProfile] Response: {req.downloadHandler?.text}");

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"POST /delete_profile failed: {req.error}");
            Debug.LogError($"Response: {req.downloadHandler?.text}");
            onDone?.Invoke(false);
            req.Dispose();
            yield break;
        }

        var resp = JsonUtility.FromJson<DeleteProfileResp>(req.downloadHandler.text);
        bool ok = (resp != null && (resp.status == "success" || resp.status == "partial"));
        Debug.Log($"[DeleteProfile] Result: {resp?.status} - {resp?.message}");
        onDone?.Invoke(ok);
        req.Dispose();
    }


    // Data Models
    [System.Serializable]
    private class ProfilesResponse
    {
        public FaceProfile[] profiles;
        public int count;
    }
    [System.Serializable]
    private class FaceProfile
    {
        public string profile_key;
        public string name;
        public string mssv;
        public string prefix;
        public int doc_count;
    }

    [System.Serializable]
    private class DeleteProfileResp
    {
        public string status;
        public string message;
        public int deleted_docs;
        public int deleted_files;
        public string prefix;
    }

    [System.Serializable]
    private class FirebaseAttendancePayload
    {
        public string name;
        public string mssv;
        public string time;
        public long timestamp;
        public string status;
        public float confidence;
        public float distance;
    }

    // [NEW] Web-like Entry Animation
    private void AnimateEntry(VisualElement container)
    {
        if (container == null) return;

        // Reset state
        container.RemoveFromClassList("fade-in-active");
        container.AddToClassList("fade-in-hidden");

        // Schedule animation start
        container.schedule.Execute(() =>
        {
            container.RemoveFromClassList("fade-in-hidden");
            container.AddToClassList("fade-in-active");
        }).StartingIn(20);
    }

    // [NEW] Style scrollbars via C# (USS không override được)
    private void StyleAllScrollbars(VisualElement root)
    {
        // Tìm tất cả dragger elements
        var draggers = root.Query<VisualElement>(className: "unity-scroller__dragger").ToList();
        foreach (var dragger in draggers)
        {
            dragger.style.width = 6;
            dragger.style.borderTopLeftRadius = 10;
            dragger.style.borderTopRightRadius = 10;
            dragger.style.borderBottomLeftRadius = 10;
            dragger.style.borderBottomRightRadius = 10;
            dragger.style.backgroundColor = new Color(0.7f, 0.75f, 0.78f); // #B2BFC7
            dragger.style.backgroundImage = null; // Xóa hình nền mặc định
        }

        // Ẩn nút mũi tên lên/xuống
        var lowButtons = root.Query<VisualElement>(className: "unity-scroller__low-button").ToList();
        var highButtons = root.Query<VisualElement>(className: "unity-scroller__high-button").ToList();
        foreach (var btn in lowButtons) btn.style.display = DisplayStyle.None;
        foreach (var btn in highButtons) btn.style.display = DisplayStyle.None;

        // Style track (đường ray)
        var sliders = root.Query<VisualElement>(className: "unity-scroller__slider").ToList();
        foreach (var slider in sliders)
        {
            slider.style.backgroundColor = Color.clear;
        }

        Debug.Log($"[StyleScrollbars] Styled {draggers.Count} draggers, hid {lowButtons.Count + highButtons.Count} buttons");
    }

    private static void SetBorderWidth(VisualElement ve, float w)
    {
        ve.style.borderTopWidth = w;
        ve.style.borderBottomWidth = w;
        ve.style.borderLeftWidth = w;
        ve.style.borderRightWidth = w;
    }

    // --- STATISTICS TAB LOGIC ---
    // [NEW] Fields for Statistics Tab
    private VisualElement _statsContainer;
    private ListView _listTopLate;
    private Button _btnExportCSV;

    private VisualElement _trendChartContainer;
    private Label _lblTrendPercent;


    // Note: _pieChartContainer, _lblPiePresent, _lblPieLate, _lblPieAbsent declared above (line ~170)

    // [NEW] Schedule & Settings Fields
    private VisualElement _scheduleContainer, _settingsContainer;
    private TextField _inpTimeStart;
    private Slider _sldThreshold;
    private Label _lblThresholdValue;
    private Button _btnSaveSettings;

    // [NEW] Setup method called in OnEnable
    void SetupStatsTab()
    {
        _statsContainer = _uiRoot.Q<VisualElement>("StatsContainer");
        _listTopLate = _uiRoot.Q<ListView>("ListTopLate");
        _listTopAbsent = _uiRoot.Q<ListView>("ListTopAbsent"); // [NEW]
        _btnExportCSV = _uiRoot.Q<Button>("BtnExportCSV");

        // [NEW] Latency Card Labels
        _lblLatencyTotal = _uiRoot.Q<Label>("LblLatencyTotal");
        _lblLatencyDetection = _uiRoot.Q<Label>("LblLatencyDetection");
        _lblLatencyAlignment = _uiRoot.Q<Label>("LblLatencyAlignment");
        _lblLatencyEmbedding = _uiRoot.Q<Label>("LblLatencyEmbedding");
        _lblLatencyMatching = _uiRoot.Q<Label>("LblLatencyMatching");
        _lblLatencyNetwork = _uiRoot.Q<Label>("LblLatencyNetwork");
        _lblLatencyNote = _uiRoot.Q<Label>("LblLatencyNote");

        // Trend Chart
        _trendChartContainer = _uiRoot.Q<VisualElement>("TrendChartContainer");
        _lblTrendPercent = _uiRoot.Q<Label>("LblTrendPercent");

        // Pie Chart
        _pieChartContainer = _uiRoot.Q<VisualElement>("PieChartContainer");
        _lblPiePresent = _uiRoot.Q<Label>("LblPiePresent");
        _lblPieLate = _uiRoot.Q<Label>("LblPieLate");
        _lblPieAbsent = _uiRoot.Q<Label>("LblPieAbsent");
        
        // [NEW] Weekly trend chart (10 weeks)
        _weeklyTrendCanvas = _uiRoot.Q<VisualElement>("WeeklyTrendCanvas");
        _lblOverviewHint = _uiRoot.Q<Label>("LblOverviewHint");
        _lblWeeklyTrendValue = _uiRoot.Q<Label>("LblWeeklyTrendValue");

        // Schedule & Settings
        _scheduleContainer = _uiRoot.Q<VisualElement>("ScheduleContainer");
        _settingsContainer = _uiRoot.Q<VisualElement>("SettingsContainer");

        _inpTimeStart = _uiRoot.Q<TextField>("InpTimeStart");
        _sldThreshold = _uiRoot.Q<Slider>("SldThreshold");
        _lblThresholdValue = _uiRoot.Q<Label>("LblThresholdValue");
        _btnSaveSettings = _uiRoot.Q<Button>("BtnSaveSettings");

        if (_btnExportCSV != null) _btnExportCSV.clicked += ExportStatsToCSV;
        if (_btnSaveSettings != null) _btnSaveSettings.clicked += SaveSettings;

        // NOTE: "Thiết lập học kỳ" button moved to Schedule tab (SetupScheduleTab)

        // Init Settings UI (Mock Load)
        if (_sldThreshold != null) _sldThreshold.RegisterValueChangedCallback(evt =>
        {
            if (_lblThresholdValue != null) _lblThresholdValue.text = evt.newValue.ToString("F1");
        });

        // [NEW] Week selector callback (Home dropdown)
        if (_drpChartWeekSelector != null)
        {
            _drpChartWeekSelector.RegisterValueChangedCallback(_ => UpdateStatsTab());
        }

        // [NEW] Week selector INSIDE Stats tab
        var drpStatsWeek = _uiRoot.Q<DropdownField>("DrpStatsWeekSelector");
        if (drpStatsWeek != null)
        {
            var options = new List<string>();
            options.Add("Tuần này");
            options.Add("Tất cả");

            DateTime startOfWeek = GetStartOfWeek(DateTime.Now);
            for (int i = 1; i <= 8; i++) // Add 8 past weeks
            {
                DateTime pastWeek = startOfWeek.AddDays(-7 * i);
                string label = $"Tuần {GetIso8601WeekOfYear(pastWeek)} ({pastWeek:dd/MM} - {pastWeek.AddDays(6):dd/MM})";
                options.Add(label);
            }

            drpStatsWeek.choices = options;
            drpStatsWeek.value = "Tuần này";
            drpStatsWeek.RegisterValueChangedCallback(evt =>
            {
                if (_syncingWeekSelectors) return;
                _syncingWeekSelectors = true;

                // ✅ Sync back to Chart week selector
                if (_drpChartWeekSelector != null && _drpChartWeekSelector.value != evt.newValue)
                    _drpChartWeekSelector.SetValueWithoutNotify(evt.newValue);

                _syncingWeekSelectors = false;

                UpdateStatsTab();
                UpdateTrendChart();
                UpdateStatsFromLogs();
            });
        }

        SetupRankingListView(_listTopLate);
        SetupRankingListView(_listTopAbsent); // [NEW]
    }



    // [NEW] Helper to parse Date Range from Dropdown
    private void GetSelectedDateRange(out DateTime start, out DateTime end)
    {
        string selection = _drpChartWeekSelector?.value ?? "Tuần này";
        start = DateTime.MinValue;
        end = DateTime.MaxValue;

        if (selection == "Tuần này")
        {
            start = GetStartOfWeek(DateTime.Now);
            end = start.AddDays(6).Date.AddHours(23).AddMinutes(59).AddSeconds(59);
            return;
        }

        if (selection == "Tất cả")
        {
            // Use min/max from phases if available
            var starts = new List<DateTime>();
            var ends = new List<DateTime>();

            if (TryParseVNDate(_inpPhase1Start?.value, out var p1s)) starts.Add(p1s);
            if (TryParseVNDate(_inpPhase2Start?.value, out var p2s)) starts.Add(p2s);
            if (TryParseVNDate(_inpPhase1End?.value, out var p1e)) ends.Add(p1e);
            if (TryParseVNDate(_inpPhase2End?.value, out var p2e)) ends.Add(p2e);

            if (starts.Count > 0 && ends.Count > 0)
            {
                start = starts.Min().Date;
                end = ends.Max().Date.AddHours(23).AddMinutes(59).AddSeconds(59);
            }
            return;
        }

        // Week selected from _weekMap (format: dd/MM/yyyy-dd/MM/yyyy)
        if (_weekMap.TryGetValue(selection, out var range))
        {
            start = range.start;
            end = range.end;
            return;
        }

        // Fallback: try old format "Tuần X (dd/MM - dd/MM)"
        try
        {
            int open = selection.IndexOf('(');
            int close = selection.IndexOf(')');
            if (open > 0 && close > open)
            {
                string dates = selection.Substring(open + 1, close - open - 1);
                string[] parts = dates.Split('-');
                string startS = parts[0].Trim();

                DateTime now = DateTime.Now;
                int year = now.Year;
                if (DateTime.TryParseExact(startS + "/" + year, "dd/MM/yyyy", null,
                    System.Globalization.DateTimeStyles.None, out DateTime parsed))
                {
                    if (parsed > now.AddDays(14)) year--;
                    start = new DateTime(year, parsed.Month, parsed.Day);
                    end = start.AddDays(6).Date.AddHours(23).AddMinutes(59).AddSeconds(59);
                    return;
                }
            }
        }
        catch { }

        // Ultimate fallback
        start = GetStartOfWeek(DateTime.Now);
        end = start.AddDays(6).Date.AddHours(23).AddMinutes(59).AddSeconds(59);
    }

    // [HOME PAGE] Grouped Bar Chart: Có mặt vs Vắng + Tooltip
    void UpdateTrendChart()
    {
        var container = _uiRoot.Q<VisualElement>("TrendChartCanvas");
        if (container == null) return;

        container.Clear();
        container.style.position = Position.Relative;

        // 1) Range theo dropdown (Tuần / ... / Tất cả)
        GetSelectedDateRange(out DateTime start, out DateTime end);

        // Limit "Tất cả" để đỡ quá dài
        DateTime today = DateTime.Today;
        if (start == DateTime.MinValue || start < today.AddDays(-365)) start = today.AddDays(-90);
        if (end == DateTime.MaxValue || end > today.AddDays(7)) end = today;

        // 2) Total students: ưu tiên roster import
        if (_studentDataList == null) _studentDataList = new List<StudentStat>();
        if (_studentDataList.Count == 0) LoadStudentDataListFromFile();

        int totalStudents = _studentDataList?.Count ?? 0;

        // Nếu chưa có roster thì không vẽ để tránh "vắng ảo"
        if (totalStudents <= 0)
        {
            var empty = new Label("📋 Chưa import danh sách lớp cho lớp này\n→ Không thể tính biểu đồ Có mặt/Vắng");
            empty.style.whiteSpace = WhiteSpace.Normal;
            empty.style.unityTextAlign = TextAnchor.MiddleCenter;
            empty.style.color = new Color(0.55f, 0.55f, 0.55f);
            empty.style.flexGrow = 1;
            container.Add(empty);
            return;
        }

        bool hasSchedule = _scheduleList != null && _scheduleList.Count > 0;

        bool IsAbsentStatus(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            return s == "absent" || s == "vắng" || s == "vang";
        }

        // 3) [FIX] Luôn hiển thị 7 ngày của tuần hiện tại (T2-CN)
        var days = new List<DateTime>();
        // Reuse 'today' variable from above
        int dow = (int)today.DayOfWeek;
        if (dow == 0) dow = 7; // Sunday = 7
        DateTime monday = today.AddDays(-(dow - 1));

        for (int i = 0; i < 7; i++)
        {
            days.Add(monday.AddDays(i));
        }

        // 4) Data: present / absent theo từng ngày
        var labels = new List<string>();
        var presentCounts = new List<int>();
        var absentCounts = new List<int>();

        foreach (var d in days)
        {
            string dStr = d.ToString("dd/MM/yyyy");

            // Present = distinct MSSV có log ngày đó (không tính status Absent)
            int present = (_allLogs ?? new List<AttendanceLog>())
                .Where(x => x.date == dStr && !string.IsNullOrEmpty(x.mssv) && !IsAbsentStatus(x.status))
                .Select(x => x.mssv.Trim())
                .Distinct()
                .Count();

            // Absent chỉ tính khi:
            // - có schedule thật (để khỏi vắng ảo)
            // - ngày đó CÓ HỌC
            // - và buổi học đã kết thúc
            int absent = 0;
            if (hasSchedule && HasClass(d) && HasClassFinished(d))
                absent = Mathf.Max(0, totalStudents - present);

            presentCounts.Add(present);
            absentCounts.Add(absent);

            // [FIX] Label format: T2, T3, ... CN with date
            string[] dayNames = { "CN", "T2", "T3", "T4", "T5", "T6", "T7" };
            int dayIdx = (int)d.DayOfWeek;
            labels.Add($"{dayNames[dayIdx]}\n{d:dd/MM}");
        }

        // 5) Tooltip overlay
        var tooltip = new VisualElement();
        tooltip.pickingMode = PickingMode.Ignore;
        tooltip.style.position = Position.Absolute;
        tooltip.style.display = DisplayStyle.None;
        tooltip.style.backgroundColor = Color.white;
        tooltip.style.paddingLeft = 10;
        tooltip.style.paddingRight = 10;
        tooltip.style.paddingTop = 8;
        tooltip.style.paddingBottom = 8;
        tooltip.style.borderTopLeftRadius = 10;
        tooltip.style.borderTopRightRadius = 10;
        tooltip.style.borderBottomLeftRadius = 10;
        tooltip.style.borderBottomRightRadius = 10;
        tooltip.style.borderLeftWidth = 1;
        tooltip.style.borderRightWidth = 1;
        tooltip.style.borderTopWidth = 1;
        tooltip.style.borderBottomWidth = 1;
        tooltip.style.borderLeftColor = new Color(0.85f, 0.87f, 0.90f);
        tooltip.style.borderRightColor = new Color(0.85f, 0.87f, 0.90f);
        tooltip.style.borderTopColor = new Color(0.85f, 0.87f, 0.90f);
        tooltip.style.borderBottomColor = new Color(0.85f, 0.87f, 0.90f);

        var ttTitle = new Label("");
        ttTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
        ttTitle.style.fontSize = 12;
        ttTitle.style.marginBottom = 6;
        ttTitle.style.color = new Color(0.2f, 0.2f, 0.2f);
        tooltip.Add(ttTitle);

        VisualElement TipRow(Color dotColor, string labelText, Label valueLabel)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.marginTop = 3;

            var left = new VisualElement();
            left.style.flexDirection = FlexDirection.Row;
            left.style.alignItems = Align.Center;

            var dot = new VisualElement();
            dot.style.width = 8;
            dot.style.height = 8;
            dot.style.backgroundColor = dotColor;
            dot.style.borderTopLeftRadius = 4;
            dot.style.borderTopRightRadius = 4;
            dot.style.borderBottomLeftRadius = 4;
            dot.style.borderBottomRightRadius = 4;
            dot.style.marginRight = 8;

            var text = new Label(labelText);
            text.style.fontSize = 11;
            text.style.color = new Color(0.3f, 0.3f, 0.3f);

            left.Add(dot);
            left.Add(text);

            valueLabel.style.fontSize = 11;
            valueLabel.style.color = new Color(0.25f, 0.25f, 0.25f);

            row.Add(left);
            row.Add(valueLabel);
            return row;
        }

        var ttPresentVal = new Label("0");
        var ttAbsentVal = new Label("0");

        Color cPresent = new Color(0.00f, 0.72f, 0.58f); // Green
        Color cAbsent = new Color(0.84f, 0.19f, 0.19f); // Red (#d63031 approx)

        tooltip.Add(TipRow(cPresent, "Có mặt", ttPresentVal));
        tooltip.Add(TipRow(cAbsent, "Vắng", ttAbsentVal));
        container.Add(tooltip);

        // 6) Graph
        var graph = new VisualElement();
        graph.style.width = Length.Percent(100);
        graph.style.height = Length.Percent(100);
        graph.pickingMode = PickingMode.Position;
        container.Add(graph);

        // Hit rects cho tooltip (group-level để hover dễ)
        var groupRects = new List<Rect>();

        void RebuildHitRects(float w, float h)
        {
            groupRects.Clear();

            float padL = 18f, padR = 18f, padT = 10f, padB = 30f;
            float chartW = Mathf.Max(1, w - padL - padR);
            float chartH = Mathf.Max(1, h - padT - padB);

            int n = labels.Count;
            float groupW = chartW / Mathf.Max(1, n);
            float barW = groupW * 0.32f;
            float gap = groupW * 0.10f;

            for (int i = 0; i < n; i++)
            {
                float baseX = padL + i * groupW;
                float blockW = (2 * barW) + gap;
                float startX = baseX + (groupW - blockW) / 2f;

                // group rect: full chart height để hover dễ (kể cả khi cột = 0)
                var r = new Rect(startX, padT, blockW, chartH);
                groupRects.Add(r);
            }
        }

        graph.RegisterCallback<GeometryChangedEvent>(_ =>
        {
            RebuildHitRects(graph.contentRect.width, graph.contentRect.height);
        });

        graph.RegisterCallback<PointerMoveEvent>(evt =>
        {
            var p = evt.localPosition;

            int idx = -1;
            for (int i = 0; i < groupRects.Count; i++)
            {
                if (groupRects[i].Contains(p))
                {
                    idx = i;
                    break;
                }
            }

            if (idx < 0)
            {
                tooltip.style.display = DisplayStyle.None;
                return;
            }

            ttTitle.text = $"Ngày {labels[idx]}";
            ttPresentVal.text = presentCounts[idx].ToString();
            ttAbsentVal.text = absentCounts[idx].ToString();

            // position tooltip (clamp trong container)
            float tw = 180f;
            float th = 70f;

            float x = p.x + 14f;
            float y = p.y - th - 10f;
            if (x + tw > container.contentRect.width) x = container.contentRect.width - tw - 8f;
            if (x < 8f) x = 8f;
            if (y < 8f) y = p.y + 14f;

            tooltip.style.left = x;
            tooltip.style.top = y;
            tooltip.style.display = DisplayStyle.Flex;
        });

        graph.RegisterCallback<PointerLeaveEvent>(_ =>
        {
            tooltip.style.display = DisplayStyle.None;
        });

        // Vẽ bars (scale theo totalStudents)
        graph.generateVisualContent += (mgc) =>
        {
            var painter = mgc.painter2D;

            float w = graph.contentRect.width;
            float h = graph.contentRect.height;

            float padL = 18f, padR = 18f, padT = 10f, padB = 30f;
            float chartW = Mathf.Max(1, w - padL - padR);
            float chartH = Mathf.Max(1, h - padT - padB);

            int n = labels.Count;
            if (n <= 0) return;

            // grid ngang
            painter.lineWidth = 1f;
            painter.strokeColor = new Color(0.90f, 0.92f, 0.94f);
            for (int i = 1; i <= 4; i++)
            {
                float y = padT + (chartH * i / 5f);
                painter.BeginPath();
                painter.MoveTo(new Vector2(padL, y));
                painter.LineTo(new Vector2(w - padR, y));
                painter.Stroke();
            }

            void FillRect(float x, float y, float rw, float rh, Color col)
            {
                if (rh <= 0.01f) return;
                painter.fillColor = col;
                painter.BeginPath();
                painter.MoveTo(new Vector2(x, y));
                painter.LineTo(new Vector2(x + rw, y));
                painter.LineTo(new Vector2(x + rw, y + rh));
                painter.LineTo(new Vector2(x, y + rh));
                painter.ClosePath();
                painter.Fill();
            }

            float groupW = chartW / n;
            float barW = groupW * 0.32f;
            float gap = groupW * 0.10f;

            for (int i = 0; i < n; i++)
            {
                float baseX = padL + i * groupW;
                float blockW = (2 * barW) + gap;
                float startX = baseX + (groupW - blockW) / 2f;

                float present = presentCounts[i];
                float absent = absentCounts[i];

                // ✅ scale theo totalStudents (không lấy max động)
                float hPresent = (present / (float)totalStudents) * chartH;
                float hAbsent = (absent / (float)totalStudents) * chartH;

                float xPresent = startX;
                float xAbsent = startX + barW + gap;

                float yPresent = padT + (chartH - hPresent);
                float yAbsent = padT + (chartH - hAbsent);

                FillRect(xPresent, yPresent, barW, hPresent, cPresent);
                FillRect(xAbsent, yAbsent, barW, hAbsent, cAbsent);
            }
        };

        // 7) Labels dưới trục X
        var labelsContainer = new VisualElement();
        labelsContainer.style.flexDirection = FlexDirection.Row;
        labelsContainer.style.justifyContent = Justify.FlexStart;   // ✅ không dùng SpaceBetween nữa
        labelsContainer.style.alignItems = Align.FlexEnd;
        labelsContainer.style.position = Position.Absolute;
        labelsContainer.style.bottom = 0;
        labelsContainer.style.left = 18;
        labelsContainer.style.right = 18;
        labelsContainer.style.height = 30;

        float cellPercent = 100f / Mathf.Max(1, labels.Count);

        for (int i = 0; i < labels.Count; i++)
        {
            var l = new Label(labels[i]); // ví dụ: "T2\n19/01"
            l.style.width = Length.Percent(cellPercent);           // ✅ chia đều theo số ngày
            l.style.flexShrink = 0;
            l.style.unityTextAlign = TextAnchor.UpperCenter;       // ✅ canh giữa đúng cột
            l.style.whiteSpace = WhiteSpace.Normal;
            l.style.fontSize = 10;
            l.style.color = new Color(0.5f, 0.5f, 0.5f);
            labelsContainer.Add(l);
        }
        container.Add(labelsContainer);

        // 8) Legend góc phải
        VisualElement LegendItem(Color c, string text)
        {
            var item = new VisualElement();
            item.style.flexDirection = FlexDirection.Row;
            item.style.alignItems = Align.Center;
            item.style.marginLeft = 10;

            var dot = new VisualElement();
            dot.style.width = 8;
            dot.style.height = 8;
            dot.style.marginRight = 6;
            dot.style.backgroundColor = c;
            dot.style.borderTopLeftRadius = 4;
            dot.style.borderTopRightRadius = 4;
            dot.style.borderBottomLeftRadius = 4;
            dot.style.borderBottomRightRadius = 4;

            var t = new Label(text);
            t.style.fontSize = 11;
            t.style.color = new Color(0.35f, 0.35f, 0.35f);

            item.Add(dot);
            item.Add(t);
            return item;
        }

        var legend = new VisualElement();
        legend.style.position = Position.Absolute;
        legend.style.top = 0;
        legend.style.right = 0;
        legend.style.flexDirection = FlexDirection.Row;
        legend.style.paddingLeft = 8;
        legend.style.paddingRight = 8;
        legend.style.paddingTop = 6;
        legend.style.paddingBottom = 6;

        legend.Add(LegendItem(cPresent, "Có mặt"));
        legend.Add(LegendItem(cAbsent, "Vắng"));
        container.Add(legend);
    }


    // [STATS TAB] Absent Students List with >3 = Rớt môn



    // [STATS TAB] Absent Students List with >3 = Rớt môn
    void UpdateAbsentList()
    {
        var container = _uiRoot.Q<ScrollView>("AbsentStudentsList");
        if (container == null) return;
        container.Clear();

        GetSelectedDateRange(out DateTime start, out DateTime end);

        var absentCounts = new Dictionary<string, int>();

        foreach (var student in _studentDataList)
        {
            int absent = 0;
            for (var d = start.Date; d <= end.Date; d = d.AddDays(1))
            {
                // [FIX] Only count absence if class has finished
                if (HasClass(d) && HasClassFinished(d))
                {
                    string dStr = d.ToString("dd/MM/yyyy");
                    bool attended = _allLogs.Any(l => (l.mssv ?? "").Trim() == (student.MSSV ?? "").Trim() && l.date == dStr);
                    if (!attended) absent++;
                }
            }
            if (absent > 0)
            {
                absentCounts[$"{student.Name}|{student.MSSV}"] = absent;
            }
        }

        var sorted = absentCounts.OrderByDescending(x => x.Value).ToList();

        if (sorted.Count == 0)
        {
            var emptyLabel = new Label("✅ Không có sinh viên vắng!");
            emptyLabel.style.color = new Color(0.2f, 0.7f, 0.4f);
            emptyLabel.style.fontSize = 14;
            emptyLabel.style.paddingTop = 20;
            emptyLabel.style.paddingLeft = 10;
            container.Add(emptyLabel);
            return;
        }

        int rank = 1;
        foreach (var kvp in sorted)
        {
            string[] parts = kvp.Key.Split('|');
            string name = parts.Length > 0 ? parts[0] : "?";
            string mssv = parts.Length > 1 ? parts[1] : "?";
            int absences = kvp.Value;

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingTop = 8;
            row.style.paddingBottom = 8;
            row.style.paddingLeft = 10;
            row.style.paddingRight = 10;
            row.style.borderBottomWidth = 1;
            row.style.borderBottomColor = new Color(0.9f, 0.9f, 0.9f);

            var lblRank = new Label($"{rank}.");
            lblRank.style.width = 25;
            lblRank.style.fontSize = 12;
            lblRank.style.color = new Color(0.5f, 0.5f, 0.5f);
            row.Add(lblRank);

            var info = new VisualElement();
            info.style.flexGrow = 1;

            var lblName = new Label(name);
            lblName.style.fontSize = 13;
            lblName.style.unityFontStyleAndWeight = FontStyle.Bold;
            lblName.style.color = new Color(0.2f, 0.2f, 0.2f);
            info.Add(lblName);

            var lblMssv = new Label(mssv);
            lblMssv.style.fontSize = 10;
            lblMssv.style.color = new Color(0.5f, 0.5f, 0.5f);
            info.Add(lblMssv);

            row.Add(info);

            var badge = new Label($"{absences} buổi");
            badge.style.fontSize = 11;
            badge.style.paddingTop = 4;
            badge.style.paddingBottom = 4;
            badge.style.paddingLeft = 8;
            badge.style.paddingRight = 8;
            badge.style.borderTopLeftRadius = 10;
            badge.style.borderTopRightRadius = 10;
            badge.style.borderBottomLeftRadius = 10;
            badge.style.borderBottomRightRadius = 10;

            if (absences > 3)
            {
                badge.style.backgroundColor = new Color(0.9f, 0.2f, 0.2f);
                badge.style.color = Color.white;
            }
            else
            {
                badge.style.backgroundColor = new Color(1f, 0.9f, 0.8f);
                badge.style.color = new Color(0.9f, 0.4f, 0f);
            }
            row.Add(badge);

            if (absences > 3)
            {
                var failBadge = new Label("⚠️ Rớt môn");
                failBadge.style.fontSize = 10;
                failBadge.style.marginLeft = 8;
                failBadge.style.color = new Color(0.9f, 0.1f, 0.1f);
                failBadge.style.unityFontStyleAndWeight = FontStyle.Bold;
                row.Add(failBadge);
            }

            container.Add(row);
            rank++;
        }
    }
    void SetupRankingListView(ListView lv)
    {
        if (lv == null) return;
        lv.makeItem = () =>
        {
            var lbl = new Label();
            lbl.RegisterCallback<ClickEvent>(evt =>
            {
                // Drill-down logic
                if (lbl.userData is KeyValuePair<string, int> kvp)
                {
                    // kvp.Key format: "Name (MSSV)"
                    // Need to parse or find student in _studentDataList
                    string key = kvp.Key;
                    int openParen = key.LastIndexOf("(");
                    if (openParen > 0)
                    {
                        string mssv = key.Substring(openParen + 1).Replace(")", "").Trim();
                        var student = _studentDataList.FirstOrDefault(s => s.MSSV == mssv);
                        if (student != null)
                        {
                            ShowStudentDetail(student);
                        }
                    }
                }
            });
            return lbl;
        };

        lv.bindItem = (e, i) =>
        {
            if (lv.itemsSource == null) return;
            var kvp = (KeyValuePair<string, int>)lv.itemsSource[i];

            var lbl = (Label)e;
            lbl.text = $"{i + 1}. {kvp.Key} - {kvp.Value} lần";
            lbl.userData = kvp; // Store data for click handler

            lbl.style.unityTextAlign = TextAnchor.MiddleLeft;
            lbl.style.paddingLeft = 10;
            lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            lbl.style.color = new Color(0.2f, 0.2f, 0.2f);

            // Hover effect hint
            lbl.tooltip = "Bấm để xem chi tiết";
            // Actually simpler: add a class for hover in USS if possible, or just ignore for now.
        };
        lv.selectionType = SelectionType.None;
    }

    // [NEW] Main Update Logic
    void UpdateStatsTab()
    {
        // ✅ Ensure data structures initialized and roster loaded
        if (_allLogs == null) _allLogs = new List<AttendanceLog>();
        if (_studentDataList == null) _studentDataList = new List<StudentStat>();
        if (_studentDataList.Count == 0) LoadStudentDataListFromFile();

        // Note: Overview Bar Removed

        // Get Range
        GetSelectedDateRange(out DateTime start, out DateTime end);
        DateTime today = DateTime.Today;

        // 2. Ranking: Top Late (Filtered by Week)
        var lateCounts = new Dictionary<string, int>();
        foreach (var log in _allLogs)
        {
            // Filter by date
            if (DateTime.TryParseExact(log.date, "dd/MM/yyyy", null, System.Globalization.DateTimeStyles.None, out DateTime dt))
            {
                if (dt < start.Date || dt > end.Date) continue;
                if (dt > today) continue; // [FIX] Skip future dates
            }

            if (log.status == "Late" || log.status == "Trễ") // Handled bilingual
            {
                string key = $"{log.studentName} ({log.mssv})";
                if (!lateCounts.ContainsKey(key)) lateCounts[key] = 0;
                lateCounts[key]++;
            }
        }
        var sortedLate = lateCounts.OrderByDescending(x => x.Value).Take(10).ToList();
        if (_listTopLate != null)
        {
            _listTopLate.itemsSource = sortedLate;
            _listTopLate.Rebuild();
        }

        // 3. [NEW] Top Absent (Filtered by Week)
        UpdateTopAbsent(start, end);

        // 4. [NEW] Update Trend Chart
        UpdateTrendChart();

        // 5. [NEW] Update Pie Chart (Stacked Bar)
        UpdatePieChart();
        
        // 6. [NEW] Update Weekly Trend Chart (10 weeks line)
        UpdateWeeklyTrendChart();

        // Note: UpdateAbsentList() removed - UI section removed
    }

    // [NEW] Update Top Absent List - counts absences per student in selected range
    // ✅ Logic tối ưu:
    // - Có TKB: chỉ tính vắng ở những NGÀY CÓ HỌC và đã KẾT THÚC buổi
    // - Không có TKB: KHÔNG suy ra "vắng full" -> chỉ tính trên các ngày đã có log (và chỉ ngày quá khứ)
    void UpdateTopAbsent(DateTime start, DateTime end)
    {
        if (_listTopAbsent == null) return;

        if (_studentDataList == null) _studentDataList = new List<StudentStat>();
        if (_studentDataList.Count == 0) LoadStudentDataListFromFile();

        // Nếu chưa import roster -> không thể xếp hạng vắng chuẩn
        if (_studentDataList.Count == 0)
        {
            _listTopAbsent.itemsSource = new List<KeyValuePair<string, int>>();
            _listTopAbsent.Rebuild();
            return;
        }

        DateTime today = DateTime.Today;
        bool hasSchedule = _scheduleList != null && _scheduleList.Count > 0;

        bool IsAbsentStatus(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            return s == "absent" || s == "vắng" || s == "vang";
        }

        // 1) Xác định "ngày tính vắng"
        var classDays = new List<DateTime>();

        if (hasSchedule)
        {
            for (var d = start.Date; d <= end.Date; d = d.AddDays(1))
            {
                if (d > today) continue; // skip future
                if (HasClass(d) && HasClassFinished(d))
                    classDays.Add(d.Date);
            }
        }
        else
        {
            // Không có TKB -> chỉ tính trên những ngày có log và là ngày quá khứ
            foreach (var log in _allLogs)
            {
                if (!DateTime.TryParseExact(log.date, "dd/MM/yyyy", null,
                        System.Globalization.DateTimeStyles.None, out DateTime dt))
                    continue;

                dt = dt.Date;
                if (dt < start.Date || dt > end.Date) continue;
                if (dt >= today) continue;

                classDays.Add(dt);
            }

            classDays = classDays.Distinct().OrderBy(x => x).ToList();
        }

        if (classDays.Count == 0)
        {
            _listTopAbsent.itemsSource = new List<KeyValuePair<string, int>>();
            _listTopAbsent.Rebuild();
            return;
        }

        // 2) Group logs theo ngày -> present set (distinct MSSV), bỏ status "Absent"
        var presentByDate = new Dictionary<string, HashSet<string>>();

        foreach (var log in _allLogs)
        {
            if (string.IsNullOrEmpty(log.mssv)) continue;

            if (!DateTime.TryParseExact(log.date, "dd/MM/yyyy", null,
                    System.Globalization.DateTimeStyles.None, out DateTime dt))
                continue;

            dt = dt.Date;
            if (dt < start.Date || dt > end.Date) continue;

            string dStr = dt.ToString("dd/MM/yyyy");

            // Chỉ quan tâm những ngày nằm trong classDays
            bool isCountedDay = classDays.Any(x => x.ToString("dd/MM/yyyy") == dStr);
            if (!isCountedDay) continue;

            if (IsAbsentStatus(log.status)) continue;

            string m = log.mssv.Trim();
            if (!presentByDate.TryGetValue(dStr, out var set))
            {
                set = new HashSet<string>();
                presentByDate[dStr] = set;
            }
            set.Add(m);
        }

        // 3) Chuẩn hóa roster theo MSSV (distinct)
        var rosterMssvs = new List<string>();
        var displayByMssv = new Dictionary<string, string>();

        foreach (var s in _studentDataList)
        {
            string m = (s.MSSV ?? "").Trim();
            if (m.Length == 0) continue;
            if (displayByMssv.ContainsKey(m)) continue;

            rosterMssvs.Add(m);
            displayByMssv[m] = $"{(s.Name ?? "").Trim()} ({m})";
        }

        // 4) Tính vắng: mỗi ngày, ai không có mặt => +1
        var absentCountByMssv = new Dictionary<string, int>();
        foreach (var m in rosterMssvs) absentCountByMssv[m] = 0;

        foreach (var d in classDays)
        {
            string dStr = d.ToString("dd/MM/yyyy");
            presentByDate.TryGetValue(dStr, out var presentSet);
            presentSet ??= new HashSet<string>();

            foreach (var m in rosterMssvs)
            {
                if (!presentSet.Contains(m))
                    absentCountByMssv[m]++;
            }
        }

        // 5) Build list top 10
        var sorted = absentCountByMssv
            .Where(k => k.Value > 0)
            .Select(k => new KeyValuePair<string, int>(displayByMssv[k.Key], k.Value))
            .OrderByDescending(k => k.Value)
            .Take(10)
            .ToList();

        _listTopAbsent.itemsSource = sorted;
        _listTopAbsent.Rebuild();
    }

    // [NEW] Option A: chỉ tính trên buổi đã KẾT THÚC
    private void ComputeSlotsForRange(
        DateTime start, DateTime end,
        out int onTimeSlots, out int lateSlots, out int absentSlots,
        out int totalSlots, out int finishedClassDays, out int pendingClassDays, out int noDataDays)
    {
        onTimeSlots = 0; lateSlots = 0; absentSlots = 0;
        totalSlots = 0; finishedClassDays = 0; pendingClassDays = 0; noDataDays = 0;

        if (_studentDataList == null) _studentDataList = new List<StudentStat>();
        if (_studentDataList.Count == 0) LoadStudentDataListFromFile();

        int totalStudents = _studentDataList?.Count ?? 0;
        if (totalStudents <= 0) return;

        DateTime today = DateTime.Today;
        bool hasSchedule = _scheduleList != null && _scheduleList.Count > 0;

        bool IsAbsentStatus(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            return s == "absent" || s == "vắng" || s == "vang";
        }

        for (var d = start.Date; d <= end.Date; d = d.AddDays(1))
        {
            if (d > today) continue;

            if (hasSchedule)
            {
                if (!HasClass(d)) continue;

                if (!HasClassFinished(d))
                {
                    pendingClassDays++;
                    continue; // Option A: chưa chốt -> chưa tính vắng
                }
            }
            else
            {
                // Không có TKB: chỉ tính trên ngày quá khứ có log
                if (d >= today) continue;
                string ds = d.ToString("dd/MM/yyyy");
                if (_allLogs == null || !_allLogs.Any(l => l.date == ds)) continue;
            }

            string dStr = d.ToString("dd/MM/yyyy");
            var dayLogs = (_allLogs ?? new List<AttendanceLog>())
                .Where(l => l.date == dStr && !string.IsNullOrEmpty(l.mssv))
                .ToList();

            // 1 bản ghi / 1 SV / 1 ngày (lấy bản sớm nhất)
            var earliest = dayLogs
                .GroupBy(l => l.mssv.Trim())
                .Select(g => g
                    .OrderBy(l => l.timestamp > 0 ? l.timestamp : long.MaxValue)
                    .ThenBy(l =>
                    {
                        if (TimeSpan.TryParse(l.time, out var ts)) return (long)ts.TotalSeconds;
                        return long.MaxValue;
                    })
                    .First())
                .ToList();

            // ✅ Nếu có TKB và buổi đã kết thúc nhưng KHÔNG có log => "Chưa điểm danh", không tính vào chart
            if (hasSchedule && earliest.Count == 0)
            {
                noDataDays++;
                continue;
            }

            finishedClassDays++;
            totalSlots += totalStudents;

            int dayOnTime = 0;
            int dayLate = 0;

            foreach (var log in earliest)
            {
                if (IsAbsentStatus(log.status)) continue;

                // Dùng threshold để suy ra Late/OnTime nếu rawStatus không nhất quán
                string computed = ComputeStatusByTime(log.time, log.status);
                if (computed == "Late") dayLate++;
                else dayOnTime++;
            }

            int presentAny = dayOnTime + dayLate;

            onTimeSlots += dayOnTime;
            lateSlots += dayLate;
            absentSlots += Mathf.Max(0, totalStudents - presentAny);
        }
    }

    void UpdatePieChart()
    {
        if (_pieChartContainer == null) return;

        _pieChartContainer.generateVisualContent = null;

        // Range theo dropdown (tuần này / tuần khác / tất cả...)
        GetSelectedDateRange(out DateTime start, out DateTime end);

        DateTime today = DateTime.Today;
        if (end > today) end = today;

        // roster
        if (_studentDataList == null) _studentDataList = new List<StudentStat>();
        if (_studentDataList.Count == 0) LoadStudentDataListFromFile();
        int totalStudents = _studentDataList?.Count ?? 0;

        if (totalStudents <= 0)
        {
            if (_lblPiePresent != null) _lblPiePresent.text = "--";
            if (_lblPieLate != null) _lblPieLate.text = "--";
            if (_lblPieAbsent != null) _lblPieAbsent.text = "--";
            if (_lblOverviewHint != null) _lblOverviewHint.text = "📋 Chưa import danh sách lớp";
            _pieChartContainer.MarkDirtyRepaint();
            return;
        }

        ComputeSlotsForRange(
            start, end,
            out int onTimeSlots, out int lateSlots, out int absentSlots,
            out int totalSlots, out int finishedDays, out int pendingDays, out int noDataDays
        );

        // Hint
        if (_lblOverviewHint != null)
            _lblOverviewHint.text = $"Đã chốt: {finishedDays} buổi • Chưa chốt: {pendingDays} buổi • Chưa điểm danh: {noDataDays} buổi";

        if (totalSlots <= 0)
        {
            if (_lblPiePresent != null) _lblPiePresent.text = "--";
            if (_lblPieLate != null) _lblPieLate.text = "--";
            if (_lblPieAbsent != null) _lblPieAbsent.text = "--";

            // vẽ thanh placeholder để UI không bị trống
            _pieChartContainer.generateVisualContent += (mgc) =>
            {
                var painter = mgc.painter2D;
                float w = _pieChartContainer.contentRect.width;
                float h = _pieChartContainer.contentRect.height;

                float pad = 6f;
                float barH = Mathf.Min(22f, h - pad * 2);
                float barY = (h - barH) / 2f;
                float barX = pad;
                float barW = Mathf.Max(10f, w - pad * 2);

                painter.fillColor = new Color(0.92f, 0.94f, 0.96f);
                painter.BeginPath();
                painter.MoveTo(new Vector2(barX, barY));
                painter.LineTo(new Vector2(barX + barW, barY));
                painter.LineTo(new Vector2(barX + barW, barY + barH));
                painter.LineTo(new Vector2(barX, barY + barH));
                painter.ClosePath();
                painter.Fill();
            };

            _pieChartContainer.MarkDirtyRepaint();
            return;
        }

        float pOnTime = (float)onTimeSlots / totalSlots;
        float pLate   = (float)lateSlots / totalSlots;
        float pAbsent = (float)absentSlots / totalSlots;

        if (_lblPiePresent != null) _lblPiePresent.text = $"{pOnTime * 100f:F0}% ({onTimeSlots})";
        if (_lblPieLate != null) _lblPieLate.text = $"{pLate * 100f:F0}% ({lateSlots})";
        if (_lblPieAbsent != null) _lblPieAbsent.text = $"{pAbsent * 100f:F0}% ({absentSlots})";

        // 100% stacked bar
        _pieChartContainer.generateVisualContent += (mgc) =>
        {
            var painter = mgc.painter2D;

            float w = _pieChartContainer.contentRect.width;
            float h = _pieChartContainer.contentRect.height;

            float pad = 6f;
            float barH = Mathf.Min(22f, h - pad * 2);
            float barY = (h - barH) / 2f;
            float barX = pad;
            float barW = Mathf.Max(10f, w - pad * 2);

            // base
            painter.fillColor = new Color(0.92f, 0.94f, 0.96f);
            painter.BeginPath();
            painter.MoveTo(new Vector2(barX, barY));
            painter.LineTo(new Vector2(barX + barW, barY));
            painter.LineTo(new Vector2(barX + barW, barY + barH));
            painter.LineTo(new Vector2(barX, barY + barH));
            painter.ClosePath();
            painter.Fill();

            float wOnTime = barW * pOnTime;
            float wLate   = barW * pLate;
            float wAbsent = barW - wOnTime - wLate;

            void FillRect(float x, float y, float rw, float rh, Color col)
            {
                if (rw <= 0.5f || rh <= 0.5f) return;
                painter.fillColor = col;
                painter.BeginPath();
                painter.MoveTo(new Vector2(x, y));
                painter.LineTo(new Vector2(x + rw, y));
                painter.LineTo(new Vector2(x + rw, y + rh));
                painter.LineTo(new Vector2(x, y + rh));
                painter.ClosePath();
                painter.Fill();
            }

            Color cOnTime = new Color(0.00f, 0.72f, 0.58f); // #00b894
            Color cLate   = new Color(0.84f, 0.19f, 0.19f); // #d63031
            Color cAbsent = new Color(0.99f, 0.80f, 0.43f); // #fdcb6e

            float x = barX;
            FillRect(x, barY, wOnTime, barH, cOnTime); x += wOnTime;
            FillRect(x, barY, wLate,   barH, cLate);   x += wLate;
            FillRect(x, barY, wAbsent, barH, cAbsent);

            // border
            painter.lineWidth = 1f;
            painter.strokeColor = new Color(0.85f, 0.87f, 0.90f);
            painter.BeginPath();
            painter.MoveTo(new Vector2(barX, barY));
            painter.LineTo(new Vector2(barX + barW, barY));
            painter.LineTo(new Vector2(barX + barW, barY + barH));
            painter.LineTo(new Vector2(barX, barY + barH));
            painter.ClosePath();
            painter.Stroke();
        };

        _pieChartContainer.MarkDirtyRepaint();
    }

    void UpdateWeeklyTrendChart()
    {
        if (_weeklyTrendCanvas == null) return;

        _weeklyTrendCanvas.generateVisualContent = null;

        // roster
        if (_studentDataList == null) _studentDataList = new List<StudentStat>();
        if (_studentDataList.Count == 0) LoadStudentDataListFromFile();
        int totalStudents = _studentDataList?.Count ?? 0;
        if (totalStudents <= 0)
        {
            _weeklyTrendCanvas.MarkDirtyRepaint();
            return;
        }

        // Anchor theo tuần hiện tại (giảng viên hay nhìn trend gần đây)
        DateTime today = DateTime.Today;
        DateTime thisWeekStart = GetStartOfWeek(today);

        const int N = 10;
        var rates = new float[N];
        var valid = new bool[N];

        for (int i = 0; i < N; i++)
        {
            DateTime ws = thisWeekStart.AddDays(-7 * (N - 1 - i));
            DateTime we = ws.AddDays(6);

            ComputeSlotsForRange(ws, we,
                out int onTimeSlots, out int lateSlots, out int absentSlots,
                out int totalSlots, out int finishedDays, out int pendingDays, out int noDataDays);

            if (totalSlots > 0)
            {
                float presentAny = (onTimeSlots + lateSlots);
                rates[i] = presentAny / totalSlots; // % có mặt (kể cả trễ)
                valid[i] = true;
            }
            else
            {
                rates[i] = 0f;
                valid[i] = false;
            }
        }

        // Label tuần này
        int lastIdx = N - 1;
        if (_lblWeeklyTrendValue != null)
        {
            _lblWeeklyTrendValue.text = valid[lastIdx] ? $"Tuần này: {rates[lastIdx] * 100f:F0}%" : "Tuần này: --";
        }

        _weeklyTrendCanvas.generateVisualContent += (mgc) =>
        {
            var painter = mgc.painter2D;

            float w = _weeklyTrendCanvas.contentRect.width;
            float h = _weeklyTrendCanvas.contentRect.height;

            float padL = 16f, padR = 12f, padT = 10f, padB = 16f;
            float chartW = Mathf.Max(1, w - padL - padR);
            float chartH = Mathf.Max(1, h - padT - padB);

            // grid 0/25/50/75/100
            painter.lineWidth = 1f;
            painter.strokeColor = new Color(0.90f, 0.92f, 0.94f);
            for (int i = 0; i <= 4; i++)
            {
                float y = padT + chartH * (i / 4f);
                painter.BeginPath();
                painter.MoveTo(new Vector2(padL, y));
                painter.LineTo(new Vector2(padL + chartW, y));
                painter.Stroke();
            }

            // line
            painter.lineWidth = 2f;
            painter.strokeColor = new Color(0.10f, 0.52f, 0.88f); // xanh dương nhẹ

            bool started = false;
            Vector2 prev = Vector2.zero;

            for (int i = 0; i < N; i++)
            {
                if (!valid[i]) { started = false; continue; }

                float x = padL + chartW * (i / (float)(N - 1));
                float y = padT + chartH * (1f - Mathf.Clamp01(rates[i]));

                var p = new Vector2(x, y);

                if (!started)
                {
                    started = true;
                    prev = p;
                }
                else
                {
                    painter.BeginPath();
                    painter.MoveTo(prev);
                    painter.LineTo(p);
                    painter.Stroke();
                    prev = p;
                }
            }

            // points
            for (int i = 0; i < N; i++)
            {
                if (!valid[i]) continue;

                float x = padL + chartW * (i / (float)(N - 1));
                float y = padT + chartH * (1f - Mathf.Clamp01(rates[i]));

                painter.fillColor = new Color(0.10f, 0.52f, 0.88f);
                painter.BeginPath();
                painter.Arc(new Vector2(x, y), 3.2f, 0, 360);
                painter.ClosePath();
                painter.Fill();
            }
        };

        _weeklyTrendCanvas.MarkDirtyRepaint();
    }


    // [NEW] Event Handlers for New Buttons

    // ✅ NEW: Excel Encoding Helper (CodePages support)
    private static bool _excelEncodingReady = false;
    private static void EnsureExcelEncoding()
    {
        if (_excelEncodingReady) return;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _excelEncodingReady = true;
        Debug.Log("[Excel] CodePagesEncodingProvider registered.");
    }

    // [NEW] Helper functions for Excel Export
    private string EscapeCsv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Contains(",") || s.Contains("\"") || s.Contains("\n") || s.Contains("\r"))
        {
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        return s;
    }

    private List<(DateTime start, DateTime end)> BuildNineWeeksFromPhaseStart(DateTime phaseStart)
    {
        var weeks = new List<(DateTime, DateTime)>();
        
        // Tính thứ 2 của tuần chứa phaseStart
        int diff = phaseStart.DayOfWeek - DayOfWeek.Monday;
        if (diff < 0) diff += 7;
        DateTime monday = phaseStart.AddDays(-diff).Date;

        for (int i = 0; i < 9; i++)
        {
            DateTime wStart = monday.AddDays(i * 7);
            DateTime wEnd = wStart.AddDays(6).AddHours(23).AddMinutes(59).AddSeconds(59);
            weeks.Add((wStart, wEnd));
        }
        return weeks;
    }

    private void ExportWeeklyAttendanceToCSV()
    {
        // 1. Xác định StartDate của Phase hiện tại
        DateTime targetPhaseStart = DateTime.MinValue;
        
        if (TryGetCurrentPhaseWeek(DateTime.Now, out int ph, out int wk, out DateTime ws, out DateTime we))
        {
             if (ph == 1) TryParseVNDate(_playerPhase1Start, out targetPhaseStart);
             else if (ph == 2) TryParseVNDate(_playerPhase2Start, out targetPhaseStart);
        }
        
        if (targetPhaseStart == DateTime.MinValue)
        {
             // Fallback
             if (!TryParseVNDate(_playerPhase1Start, out targetPhaseStart))
                 targetPhaseStart = DateTime.Today; 
        }
        
        var weeks = BuildNineWeeksFromPhaseStart(targetPhaseStart);
        
        // 2. Build CSV
        StringBuilder sb = new StringBuilder();
        
        // Header
        sb.Append("STT,Họ và Tên,MSSV");
        for (int i=0; i < weeks.Count; i++)
        {
             sb.Append($",Tuần {i+1} ({weeks[i].start:dd/MM})");
        }
        sb.AppendLine();
        
        // Prepare Data
        if (_studentDataList == null || _studentDataList.Count == 0)
        {
             LoadStudentDataListFromFile();
        }

        // Cache logs for performance
        // Filter out invalid dates once
        var validLogs = _allLogs.Where(x => !string.IsNullOrEmpty(x.mssv) && !string.IsNullOrEmpty(x.date)).ToList();

        int stt = 1;
        foreach (var student in _studentDataList)
        {
            sb.Append($"{stt},{EscapeCsv(student.Name)},{EscapeCsv(student.MSSV)}");
            
            foreach (var week in weeks)
            {
                // Find logs for this student in this week
                // Cần parse log.date (dd/MM/yyyy) -> DateTime
                string cellStatus = "";
                bool weekEnded = week.end.Date < DateTime.Today;
                if (weekEnded) cellStatus = "Vắng"; 

                // Check logs
                bool hasPresent = false;
                bool hasLate = false;

                foreach (var log in validLogs)
                {
                    if (log.mssv == student.MSSV)
                    {
                        if (TryParseVNDate(log.date, out DateTime ld))
                        {
                            if (ld >= week.start && ld <= week.end)
                            {
                                if (log.status == "OnTime" || log.status == "Present" || log.status == "Có") hasPresent = true;
                                else if (log.status == "Late" || log.status == "Trễ") hasLate = true;
                            }
                        }
                    }
                }

                if (hasPresent) cellStatus = "Có";
                else if (hasLate) cellStatus = "Trễ";

                sb.Append($",{EscapeCsv(cellStatus)}");
            }
            sb.AppendLine();
            stt++;
        }
        
        // 3. Save and Open
        string fileName = $"BaoCao_DiemDanh_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        string filePath = Path.Combine(Application.persistentDataPath, fileName);
        
        // Write with BOM for Excel
        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
        
        ShowToast($"✅ Đã xuất file: {fileName}", colGreen);
        Application.OpenURL(filePath);

    }

    void OnImportStudentClick()
    {
        // Allow CSV and Excel
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        string path = UI.FilePicker.OpenFile("Import Danh sách Lớp", "Files\0*.csv;*.xlsx;*.xls\0", "csv;xlsx;xls");
        path = path?.TrimEnd('\0');   // ✅ FIX: Remove trailing \0 from FilePicker
        if (!string.IsNullOrEmpty(path))
        {
            StartCoroutine(ImportStudentsFromCSV(path));
        }
#else
        ShowToast("📂 Import chỉ hỗ trợ trên Windows PC", colOrange);
#endif
    }

    // [NEW] Helper to read Excel/CSV
    List<(string, string)> ReadStudentDataFromFile(string path)
    {
        Debug.Log($"[Import Debug] Reading file: {path}");
        var results = new List<(string, string)>();
        string ext = Path.GetExtension(path).ToLower();
        Debug.Log($"[Import Debug] Extension: {ext}");

        if (ext == ".csv" || ext == ".txt")
        {
            // === READ CSV ===
            try
            {
                Debug.Log("[Import Debug] Reading as CSV/Text");
                var lines = File.ReadAllLines(path, Encoding.UTF8);
                Debug.Log($"[Import Debug] Read {lines.Length} lines");
                if (lines.Length <= 1) return results; // Header only or empty

                // Parse Header
                var headerCols = lines[0].ToLower().Split(',');
                int idxName = -1, idxMssv = -1;

                for (int i = 0; i < headerCols.Length; i++)
                {
                    string c = headerCols[i].Trim();
                    if (c.Contains("tên") || c.Contains("name")) idxName = i;
                    else if (c.Contains("mssv") || c.Contains("mã") || c.Contains("id")) idxMssv = i;
                }

                if (idxName == -1 && idxMssv == -1) { idxName = 0; idxMssv = 1; }
                else
                {
                    if (idxName == -1) idxName = (idxMssv == 0) ? 1 : 0;
                    if (idxMssv == -1) idxMssv = (idxName == 0) ? 1 : 0;
                }

                Debug.Log($"[Import Debug] CSV Columns - Name: {idxName}, MSSV: {idxMssv}");

                foreach (var line in lines.Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var cols = line.Split(',');
                    int max = Mathf.Max(idxName, idxMssv);
                    if (cols.Length <= max) continue;

                    results.Add((cols[idxName].Trim(), cols[idxMssv].Trim()));
                }
            }
            catch (Exception e) { Debug.LogError("Read CSV Error: " + e.Message); }
        }
        else if (ext == ".xlsx" || ext == ".xls")
        {
            // === READ EXCEL ===
            try
            {
                Debug.Log("[Import Debug] Reading as Excel");
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    Debug.Log("[Import Debug] Stream opened");
                    EnsureExcelEncoding(); // ✅ FIX: Register CodePages encoding
                    using (var reader = ExcelReaderFactory.CreateReader(stream))
                    {
                        Debug.Log("[Import Debug] ExcelReader created");
                        var ds = reader.AsDataSet();
                        if (ds == null) { Debug.LogError("[Import Debug] DataSet is null!"); return results; }

                        Debug.Log($"[Import Debug] Tables count: {ds.Tables.Count}");
                        if (ds.Tables.Count > 0)
                        {
                            var table = ds.Tables[0];
                            Debug.Log($"[Import Debug] Rows: {table.Rows.Count}, Cols: {table.Columns.Count}");
                            if (table.Rows.Count > 0)
                            {
                                // --- SMART HEADER SCANNING (Scan first 20 rows) ---
                                int headerRowIdx = -1;
                                int idxName = -1, idxMssv = -1;

                                int scanLimit = Mathf.Min(20, table.Rows.Count);

                                for (int r = 0; r < scanLimit; r++)
                                {
                                    var rowVals = table.Rows[r];
                                    int tempName = -1, tempMssv = -1;

                                    // Check columns in this row
                                    for (int c = 0; c < table.Columns.Count; c++)
                                    {
                                        string cell = rowVals[c].ToString().ToLower().Trim();
                                        if (string.IsNullOrEmpty(cell)) continue;

                                        // [FIX] Scan independently - don't use else if
                                        // Check for MSSV column first (exact keywords)
                                        if (tempMssv == -1 && (cell.Contains("mssv") ||
                                            cell.Contains("mã sv") || cell.Contains("ma sv") ||
                                            cell.Contains("mã số sv") || cell.Contains("ma so sv") ||
                                            cell.Contains("mã sinh") || cell.Contains("sinh viên") ||
                                            cell.Contains("student id")))
                                        {
                                            tempMssv = c;
                                            Debug.Log($"[Import Debug] Row {r}, Col {c}: MSSV matched with '{cell}'");
                                        }
                                        // Check for Name column (họ, tên keywords) - but NOT if it's the MSSV column
                                        if (tempName == -1 && c != tempMssv && (cell.Contains("họ") || cell.Contains("tên") || cell.Contains("name")))
                                        {
                                            tempName = c;
                                            Debug.Log($"[Import Debug] Row {r}, Col {c}: Name matched with '{cell}'");
                                        }
                                    }

                                    // If we found BOTH or strong evidence
                                    if (tempName != -1 && tempMssv != -1)
                                    {
                                        headerRowIdx = r;
                                        idxName = tempName;
                                        idxMssv = tempMssv;
                                        Debug.Log($"[Import Debug] Found Header at Row {r}: NameCol={idxName}, MssvCol={idxMssv}");
                                        break;
                                    }
                                }

                                // Fallback: If scan failed, use default Row 0 logic (or default 0,1 cols)
                                if (headerRowIdx == -1)
                                {
                                    Debug.LogWarning("[Import Debug] Could not auto-detect header row. Using Default Row 0.");
                                    headerRowIdx = 0; // fallback
                                                      // Try to guess from indexes (defaults)
                                    idxName = 0;
                                    idxMssv = 1;
                                }

                                Debug.Log($"[Import Debug] Final Config - StartRow: {headerRowIdx + 1}, NameCol: {idxName}, MSSVCol: {idxMssv}");

                                // Iterate Rows
                                for (int i = headerRowIdx + 1; i < table.Rows.Count; i++)
                                {
                                    var row = table.Rows[i];
                                    // Check bound
                                    if (idxName >= row.ItemArray.Length || idxMssv >= row.ItemArray.Length) continue;

                                    string n = row[idxName]?.ToString().Trim();
                                    string m = row[idxMssv]?.ToString().Trim();

                                    // [FIX] Smart Merge (robust):
                                    // Tìm thêm phần "Tên" ở các cột phía sau idxName, bỏ qua MSSV/cột số/cột trống.
                                    // Hợp với format: MSSV | Họ lót | Tên  (và cả trường hợp có cột trống giữa do merge)
                                    for (int c = idxName + 1; c < row.ItemArray.Length && c <= idxName + 3; c++)
                                    {
                                        if (c == idxMssv) continue;

                                        string part = row[c]?.ToString().Trim();
                                        if (string.IsNullOrEmpty(part)) continue;
                                        if (char.IsDigit(part[0])) continue; // tránh dính STT/MSSV

                                        // Header có thể trống do merge, vẫn coi là name
                                        string h = "";
                                        if (headerRowIdx >= 0 && c < table.Columns.Count)
                                            h = table.Rows[headerRowIdx][c]?.ToString().ToLower().Trim() ?? "";

                                        bool looksLikeNameCol =
                                            string.IsNullOrWhiteSpace(h) ||
                                            h.Contains("tên") || h.Contains("ten") ||
                                            h.Contains("name") ||
                                            h.Contains("họ") || h.Contains("ho") ||
                                            h.Contains("họ và tên") || h.Contains("ho va ten");

                                        if (looksLikeNameCol)
                                        {
                                            n = (n + " " + part).Trim();
                                            break;
                                        }
                                    }

                                    if (!string.IsNullOrEmpty(n) && !string.IsNullOrEmpty(m))
                                    {
                                        results.Add((n, m));
                                    }
                                }
                            }
                            else
                            {
                                Debug.LogError("[Import Debug] Table is empty (Rows=0).");
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Import Debug] Read Excel Exception: " + e.ToString());
                ShowToast("❌ Lỗi đọc Excel: " + e.Message, colRed);
            }
        }

        Debug.Log($"[Import Debug] Found {results.Count} students");

        // Clean up STT in Name if any
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            string n = r.Item1;
            // Fix ReadOnlySpan error: use char array split
            if (char.IsDigit(n[0]) && n.Contains("."))
            {
                var parts = n.Split(new char[] { '.' }, 2);
                if (parts.Length > 1)
                {
                    results[i] = (parts[1].Trim(), r.Item2);
                }
            }
        }
        return results;
    }

    IEnumerator ImportStudentsFromCSV(string path)
    {
        Debug.Log("[Import Debug] Starting Import Coroutine with path: " + path);
        ShowToast("📂 Đang đọc danh sách lớp...", colBlue);
        yield return null;

        if (!File.Exists(path))
        {
            Debug.LogError("[Import Debug] File not found: " + path);
            yield break;
        }

        // Call Helper
        var students = ReadStudentDataFromFile(path);

        if (students.Count == 0)
        {
            Debug.LogWarning("[Import Debug] No valid data found.");
            ShowToast("⚠️ Không tìm thấy dữ liệu hợp lệ (Cần cột Name/MSSV)!", colOrange);
            yield break;
        }

        // ✅ MERGE import: update existing by MSSV + add new, KHÔNG clear để tránh "đè"
        if (_studentDataList == null) _studentDataList = new List<StudentStat>();

        System.Func<string, string> Norm = (string x) => (x ?? "").Trim();

        // Map existing by MSSV
        var oldMap = new Dictionary<string, StudentStat>();
        foreach (var sOld in _studentDataList)
        {
            var k = Norm(sOld.MSSV);
            if (k.Length == 0) continue;
            if (!oldMap.ContainsKey(k)) oldMap[k] = sOld;
        }

        // Build new list: imported first (keeps import order)
        var newList = new List<StudentStat>();
        var importedKeys = new HashSet<string>();

        foreach (var tup in students)
        {
            string nameNew = Norm(tup.Item1);
            string mssvNew = Norm(tup.Item2);
            if (mssvNew.Length == 0) continue;

            importedKeys.Add(mssvNew);

            if (oldMap.TryGetValue(mssvNew, out var existed))
            {
                // ✅ preserve existed fields, only update name if import has better name
                if (!string.IsNullOrEmpty(nameNew))
                    existed.Name = nameNew;

                newList.Add(existed);
            }
            else
            {
                // New student
                newList.Add(new StudentStat
                {
                    Name = nameNew,
                    MSSV = mssvNew,
                    TotalCheckIn = 0,
                    LateCount = 0,
                    OnTimeCount = 0,
                    AbsentCount = 0,
                    IsRegistered = false
                });
            }
        }

        // ✅ keep students that were already in roster but NOT in this import (avoid data loss)
        foreach (var sOld in _studentDataList)
        {
            var k = Norm(sOld.MSSV);
            if (k.Length == 0) continue;
            if (!importedKeys.Contains(k))
                newList.Add(sOld);
        }

        _studentDataList = newList;
        Debug.Log($"[Import] Merged roster: {_studentDataList.Count} students (imported {students.Count})");

        // [NEW] Save to local file for persistence
        SaveStudentDataListToFile();
        StartCoroutine(PushRosterToServer(silent: true)); // Sync lên server

        // Show success toast
        ShowToast($"✅ Đã import {students.Count} sinh viên!", colGreen);

        // === SYNC ALL RELATED UI ===
        FetchAndRenderStudentList();
        UpdateStatsFromLogs();

        // Refresh Register tab + Class list UI
        _skipNextProfilesFetchInRegisterTab = true; // reuse cache fetched by FetchAndRenderStudentList
        RenderRegisterTabContent();

        // Refresh ListView if available
        if (_studentListView != null)
        {
            _studentListView.itemsSource = _studentDataList;
            _studentListView.Rebuild();
        }
    }

    // [NEW] Save student list to local file
    void SaveStudentDataListToFile()
    {
        try
        {
            string filePath = Path.Combine(Application.persistentDataPath, $"studentList_{_currentTeacherId}_{CurrentTermId}_{_currentClassCode}.json");
            var data = new StudentDataListWrapper { students = _studentDataList.Select(s => new StudentDataItem { name = s.Name, mssv = s.MSSV }).ToList() };
            string json = JsonUtility.ToJson(data, true);
            File.WriteAllText(filePath, json, Encoding.UTF8);
            Debug.Log($"[Import] Saved student list to: {filePath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Import] Failed to save student list: {e.Message}");
        }
    }

    // [NEW] Load student list from local file
    void LoadStudentDataListFromFile()
    {
        try
        {
            string filePath = Path.Combine(Application.persistentDataPath, $"studentList_{_currentTeacherId}_{CurrentTermId}_{_currentClassCode}.json");
            if (File.Exists(filePath))
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                var data = JsonUtility.FromJson<StudentDataListWrapper>(json);
                if (data != null && data.students != null)
                {
                    _studentDataList.Clear();
                    foreach (var item in data.students)
                    {
                        _studentDataList.Add(new StudentStat
                        {
                            Name = item.name,
                            MSSV = item.mssv,
                            TotalCheckIn = 0,
                            LateCount = 0,
                            OnTimeCount = 0,
                            AbsentCount = 0,
                            IsRegistered = false
                        });
                    }
                    Debug.Log($"[Import] Loaded {_studentDataList.Count} students from file");
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[Import] Failed to load student list: {e.Message}");
        }
    }

    [System.Serializable]
    class StudentDataListWrapper
    {
        public List<StudentDataItem> students;
    }

    [System.Serializable]
    class StudentDataItem
    {
        public string name;
        public string mssv;
    }

    // ========== MANUAL CHECKIN MODAL ==========
    private VisualElement _modalManualCheckin;
    private ScrollView _manualCheckinStudentList;
    private TextField _inpSearchManualCheckin;
    private DropdownField _drpManualStatus;
    private Label _lblManualCheckinCount;
    private HashSet<string> _selectedMssvForManualCheckin = new HashSet<string>();

    void OnManualCheckinClick()
    {
        _modalManualCheckin = _uiRoot.Q<VisualElement>("ModalManualCheckin");
        if (_modalManualCheckin == null)
        {
            ShowToast("❌ Không tìm thấy giao diện điểm danh thủ công!", colRed);
            return;
        }

        // Show modal
        _modalManualCheckin.style.display = DisplayStyle.Flex;

        // [NEW] Animate modal card
        var modalCard = _modalManualCheckin.Q<VisualElement>("ManualCheckinCard");
        if (modalCard != null)
        {
            modalCard.style.opacity = 0;
            modalCard.transform.scale = Vector3.one * 0.9f;
            modalCard.transform.position = new Vector3(0, 30, 0);

            _modalManualCheckin.schedule.Execute(() =>
            {
                modalCard.style.opacity = 1;
                modalCard.transform.scale = Vector3.one;
                modalCard.transform.position = Vector3.zero;
            }).StartingIn(50);
        }

        // Bind UI elements
        _manualCheckinStudentList = _modalManualCheckin.Q<ScrollView>("ManualCheckinStudentList");
        _inpSearchManualCheckin = _modalManualCheckin.Q<TextField>("InpSearchManualCheckin");
        _drpManualStatus = _modalManualCheckin.Q<DropdownField>("DrpManualStatus");
        _lblManualCheckinCount = _modalManualCheckin.Q<Label>("LblManualCheckinCount");

        // Setup status dropdown
        if (_drpManualStatus != null)
        {
            _drpManualStatus.choices = new List<string> { "✅ Đúng giờ (OnTime)", "🕐 Trễ (Late)" };
            _drpManualStatus.value = "✅ Đúng giờ (OnTime)";
        }

        // Clear previous selections
        _selectedMssvForManualCheckin.Clear();
        UpdateManualCheckinCount();

        // Load students
        RenderManualCheckinStudentList("");

        // Bind search
        if (_inpSearchManualCheckin != null)
        {
            _inpSearchManualCheckin.value = "";
            _inpSearchManualCheckin.RegisterValueChangedCallback(evt =>
            {
                RenderManualCheckinStudentList(evt.newValue?.Trim() ?? "");
            });
        }

        // Bind close button
        var btnClose = _modalManualCheckin.Q<Button>("BtnCloseManualCheckin");
        if (btnClose != null)
        {
            btnClose.clicked -= CloseManualCheckinModal;
            btnClose.clicked += CloseManualCheckinModal;
        }

        // Bind confirm button
        var btnConfirm = _modalManualCheckin.Q<Button>("BtnConfirmManualCheckin");
        if (btnConfirm != null)
        {
            btnConfirm.clicked -= ConfirmManualCheckin;
            btnConfirm.clicked += ConfirmManualCheckin;
        }
    }

    void CloseManualCheckinModal()
    {
        if (_modalManualCheckin != null)
            _modalManualCheckin.style.display = DisplayStyle.None;
    }

    void RenderManualCheckinStudentList(string searchText)
    {
        if (_manualCheckinStudentList == null) return;
        _manualCheckinStudentList.Clear();

        var filtered = _allProfilesCache;
        if (!string.IsNullOrEmpty(searchText))
        {
            string lower = searchText.ToLower();
            filtered = _allProfilesCache.Where(p =>
                (p.name?.ToLower().Contains(lower) ?? false) ||
                (p.mssv?.ToLower().Contains(lower) ?? false)
            ).ToList();
        }

        if (filtered.Count == 0)
        {
            var emptyLabel = new Label("Không tìm thấy sinh viên nào.");
            emptyLabel.style.color = ColorFromHex("#636e72");
            emptyLabel.style.fontSize = 14;
            emptyLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            emptyLabel.style.paddingTop = 30;
            _manualCheckinStudentList.Add(emptyLabel);
            return;
        }

        foreach (var student in filtered)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingTop = 12;
            row.style.paddingBottom = 12;
            row.style.paddingLeft = 10;
            row.style.paddingRight = 10;
            row.style.borderBottomWidth = 1;
            row.style.borderBottomColor = ColorFromHex("#e9ecef");
            row.style.backgroundColor = _selectedMssvForManualCheckin.Contains(student.mssv)
                ? ColorFromHex("#e3f2fd")
                : Color.clear;

            // Checkbox
            var toggle = new Toggle();
            toggle.value = _selectedMssvForManualCheckin.Contains(student.mssv);
            toggle.style.marginRight = 15;
            string mssv = student.mssv; // capture for closure
            toggle.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue)
                    _selectedMssvForManualCheckin.Add(mssv);
                else
                    _selectedMssvForManualCheckin.Remove(mssv);
                UpdateManualCheckinCount();
                row.style.backgroundColor = evt.newValue ? ColorFromHex("#e3f2fd") : Color.clear;
            });
            row.Add(toggle);

            // Avatar
            var avatar = new VisualElement();
            avatar.style.width = 40;
            avatar.style.height = 40;
            avatar.style.borderTopLeftRadius = 20; avatar.style.borderTopRightRadius = 20;
            avatar.style.borderBottomLeftRadius = 20; avatar.style.borderBottomRightRadius = 20;
            avatar.style.backgroundColor = ColorFromHex("#6c5ce7");
            avatar.style.justifyContent = Justify.Center;
            avatar.style.alignItems = Align.Center;
            avatar.style.marginRight = 15;

            var avatarLetter = new Label(student.name?.Length > 0 ? student.name[0].ToString().ToUpper() : "?");
            avatarLetter.style.color = Color.white;
            avatarLetter.style.fontSize = 16;
            avatarLetter.style.unityFontStyleAndWeight = FontStyle.Bold;
            avatar.Add(avatarLetter);
            row.Add(avatar);

            // Info
            var info = new VisualElement();
            info.style.flexGrow = 1;

            var lblName = new Label(student.name ?? "Unknown");
            lblName.style.fontSize = 15;
            lblName.style.unityFontStyleAndWeight = FontStyle.Bold;
            lblName.style.color = ColorFromHex("#2d3436");
            info.Add(lblName);

            var lblMssv = new Label(student.mssv ?? "");
            lblMssv.style.fontSize = 12;
            lblMssv.style.color = ColorFromHex("#636e72");
            info.Add(lblMssv);

            row.Add(info);

            // Click row to toggle
            row.RegisterCallback<ClickEvent>(evt =>
            {
                toggle.value = !toggle.value;
            });

            _manualCheckinStudentList.Add(row);
        }
    }

    void UpdateManualCheckinCount()
    {
        if (_lblManualCheckinCount != null)
        {
            int count = _selectedMssvForManualCheckin.Count;
            _lblManualCheckinCount.text = $"Đã chọn: {count} sinh viên";
        }
    }

    void ConfirmManualCheckin()
    {
        if (_selectedMssvForManualCheckin.Count == 0)
        {
            ShowToast("⚠️ Vui lòng chọn ít nhất 1 sinh viên!", colOrange);
            return;
        }

        // Determine status
        string statusValue = _drpManualStatus?.value ?? "OnTime";
        string status = statusValue.Contains("Trễ") ? "Late" : "OnTime";

        // Start batch checkin
        StartCoroutine(BatchManualCheckin(_selectedMssvForManualCheckin.ToList(), status));
    }

    IEnumerator BatchManualCheckin(List<string> mssvList, string status)
    {
        int successCount = 0;
        int failCount = 0;

        foreach (var mssv in mssvList)
        {
            var student = _allProfilesCache.FirstOrDefault(p => p.mssv == mssv);
            if (student == null) { failCount++; continue; }

            // Create manual log entry
            var now = DateTime.Now;
            // [FIX] Use ID format matching Server (yyyy-MM-dd_MSSV) to imply deduplication
            string dateIso = now.ToString("yyyy-MM-dd");
            var log = new AttendanceLog
            {
                id = $"{dateIso}_{mssv}",
                studentName = student.name,
                mssv = mssv,
                time = now.ToString("HH:mm:ss"),
                date = now.ToString("dd/MM/yyyy"), // UI uses dd/MM/yyyy
                status = status,
                confidence = 100f,
                timestamp = now.Ticks, // [FIX] Use Ticks to match Server format for Sorting
                isSynced = false // [NEW] Mark as pending sync
            };

            // 1. Add to local logs immediately (for instant UI feedback)
            _allLogs.Insert(0, log);
            successCount++;

            // 2. Send to server (non-blocking)
            StartCoroutine(PostManualLogToServer(log));

            yield return null; // Spread load across frames
        }

        // Close modal
        CloseManualCheckinModal();

        // === CRITICAL: SYNC ALL UI COMPONENTS ===

        // 3. Save logs to local file (persist across app restart)
        SaveLogsLocal();

        // 4. Refresh Attendance Log ListView
        RefreshMainContent();

        // 5. Update Home Page Stats (Total, Present, Absent, Late, Percent)
        UpdateStatsFromLogs();

        // 6. Update Trend Chart if visible
        UpdateTrendChart();

        // 7. Update Stats Tab if cached
        UpdateStatsTab();

        // Show result
        ShowToast($"✅ Đã điểm danh {successCount} sinh viên ({status})", colGreen);

        if (failCount > 0)
        {
            ShowToast($"⚠️ {failCount} sinh viên không tìm thấy trong danh sách", colOrange);
        }
    }

    // [NEW] POST manual log to server API
    IEnumerator PostManualLogToServer(AttendanceLog log)
    {
        // Prepare request data
        var requestData = new
        {
            mssv = log.mssv,
            name = log.studentName,
            time = log.time,
            date = log.date,
            status = log.status,
            confidence = log.confidence,
            manual = true
        };

        string json = JsonUtility.ToJson(new ManualLogRequest
        {
            mssv = log.mssv,
            name = log.studentName,
            time = log.time,
            date = log.date,
            status = log.status,
            confidence = log.confidence,
            manual = true
        });

        using (var request = new UnityWebRequest($"{cloudRunApiUrl}/log_attendance", "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 10;

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[ManualCheckin] Failed to sync to server: {request.error}");
                // Data is still saved locally, will sync on next fetch
            }
            else
            {
                Debug.Log($"[ManualCheckin] Synced to server: {log.studentName}");
            }
        }
    }

    [System.Serializable]
    private class ManualLogRequest
    {
        public string mssv;
        public string name;
        public string time;
        public string date;
        public string status;
        public float confidence;
        public bool manual;
    }

    void SaveSettings()
    {
        string time = _inpTimeStart != null ? _inpTimeStart.value : "07:00";
        float threshold = _sldThreshold != null ? _sldThreshold.value : 0.6f;

        // Save to PlayerPrefs
        PlayerPrefs.SetString("ClassStartTime", time);
        PlayerPrefs.SetFloat("AIThreshold", threshold);
        PlayerPrefs.Save();

        ShowToast("✅ Đã lưu cài đặt hệ thống!", colGreen);
    }

    // ============================================================
    // SCHEDULE MANAGEMENT METHODS
    // ============================================================

    [SerializeField] private VisualTreeAsset modalAddSessionTemplate;

    void SetupScheduleTab()
    {
        // Query buttons
        _btnImportSchedule = _uiRoot.Q<Button>("BtnImportSchedule");
        _btnAddSession = _uiRoot.Q<Button>("BtnAddSession");
        _btnClearSchedule = _uiRoot.Q<Button>("BtnClearSchedule");
        _todayScheduleContainer = _uiRoot.Q<VisualElement>("TodayScheduleContainer");

        // Query schedule columns (Thứ 2-7)
        for (int i = 0; i < 6; i++)
        {
            _scheduleCols[i] = _uiRoot.Q<VisualElement>($"ScheduleCol{i + 2}");
        }

        // Bind events
        if (_btnImportSchedule != null) _btnImportSchedule.clicked += OnImportScheduleClick;
        if (_btnAddSession != null) _btnAddSession.clicked += OnAddSessionClick;
        if (_btnClearSchedule != null) _btnClearSchedule.clicked += OnClearScheduleClick;

        // ✅ Add "Thiết lập học kỳ" next to Import Schedule button (in Schedule tab)
        var existing = _uiRoot.Q<Button>("BtnEditTerm");
        if (existing == null && _btnImportSchedule != null)
        {
            var parent = _btnImportSchedule.parent;
            if (parent != null)
            {
                var btnEditTerm = new Button() { name = "BtnEditTerm", text = "⚙️ Thiết lập học kỳ" };
                btnEditTerm.style.height = 40;
                btnEditTerm.style.marginLeft = 10;
                btnEditTerm.style.backgroundColor = ColorFromHex("#f1f2f6");
                btnEditTerm.style.borderTopLeftRadius = 10;
                btnEditTerm.style.borderTopRightRadius = 10;
                btnEditTerm.style.borderBottomLeftRadius = 10;
                btnEditTerm.style.borderBottomRightRadius = 10;
                btnEditTerm.style.borderLeftWidth = 0;
                btnEditTerm.style.borderRightWidth = 0;
                btnEditTerm.style.borderTopWidth = 0;
                btnEditTerm.style.borderBottomWidth = 0;
                btnEditTerm.style.color = ColorFromHex("#2d3436");
                btnEditTerm.style.unityFontStyleAndWeight = FontStyle.Bold;

                btnEditTerm.clicked += ShowTermSetupModal;

                int idx = parent.IndexOf(_btnImportSchedule);
                parent.Insert(idx + 1, btnEditTerm);
            }
        }

        // Load saved schedule
        LoadScheduleFromLocal();
        RenderScheduleTab();
        RenderTodaySchedule();
    }



    void RenderScheduleTab()
    {
        // Clear all columns
        for (int i = 0; i < 6; i++)
        {
            if (_scheduleCols[i] == null) continue;
            _scheduleCols[i].Clear();
        }

        // Group sessions by dayOfWeek
        var grouped = _scheduleList.GroupBy(s => s.dayOfWeek);

        // Dictionary to track processed columns to ensure we fill empty ones too if needed, 
        // but currently we loop through groups. 
        // Better: Loop through columns 0-5 (Mon-Sat) to ensure structure is consistent even for empty days.
        for (int i = 0; i < 6; i++)
        {
            if (_scheduleCols[i] == null) continue;

            int dayOfWeek = i + 2; // Index 0 = Mon(2)

            // Create Visual Structure: Morning Block (Fixed Min Height) + Afternoon Block
            var morningBlock = new VisualElement();
            morningBlock.style.minHeight = 220; // Fixed height to ensure alignment
            morningBlock.style.flexDirection = FlexDirection.Column;
            morningBlock.style.justifyContent = Justify.FlexStart;
            morningBlock.style.borderBottomWidth = 1;
            morningBlock.style.borderBottomColor = new StyleColor(new Color(0.9f, 0.9f, 0.9f));
            morningBlock.style.marginBottom = 10;
            morningBlock.style.paddingBottom = 10;

            var afternoonBlock = new VisualElement();
            afternoonBlock.style.flexDirection = FlexDirection.Column;
            afternoonBlock.style.justifyContent = Justify.FlexStart;

            _scheduleCols[i].Add(morningBlock);
            _scheduleCols[i].Add(afternoonBlock);

            // Find sessions for this day
            var group = grouped.FirstOrDefault(g => g.Key == dayOfWeek);
            var morningSessions = new List<ScheduleSession>();
            var afternoonSessions = new List<ScheduleSession>();

            if (group != null)
            {
                morningSessions = group.Where(s => GetStartPeriodFromSession(s) >= 1 && GetStartPeriodFromSession(s) <= 6).OrderBy(s => GetStartPeriodFromSession(s)).ToList();
                afternoonSessions = group.Where(s => GetStartPeriodFromSession(s) > 6).OrderBy(s => GetStartPeriodFromSession(s)).ToList();
            }

            // Render Morning
            if (morningSessions.Any())
            {
                foreach (var session in morningSessions)
                {
                    var card = CreateSessionCard(session);
                    morningBlock.Add(card);
                }
            }
            else
            {
                // Add placeholder if there are afternoon sessions (or just always to keep grid?)
                // User wants synchronization. The block min-height handles the space. 
                // We add placeholder TEXT only if user needs to know "No morning class"

                var placeholder = new VisualElement();
                placeholder.style.flexGrow = 1; // Fill the morning block
                placeholder.style.backgroundColor = new StyleColor(new Color(0.98f, 0.98f, 0.98f));
                placeholder.style.borderTopLeftRadius = 6;
                placeholder.style.borderTopRightRadius = 6;
                placeholder.style.borderBottomLeftRadius = 6;
                placeholder.style.borderBottomRightRadius = 6;
                placeholder.style.alignItems = Align.Center;
                placeholder.style.justifyContent = Justify.Center;

                var lblEmpty = new Label("(Trống)");
                lblEmpty.style.color = new Color(0.7f, 0.7f, 0.7f);
                lblEmpty.style.fontSize = 11;
                lblEmpty.style.unityFontStyleAndWeight = FontStyle.Italic;
                placeholder.Add(lblEmpty);

                morningBlock.Add(placeholder);
            }

            // Render Afternoon
            foreach (var session in afternoonSessions)
            {
                var card = CreateSessionCard(session);
                afternoonBlock.Add(card);
            }
        }

        // Update date headers based on current week
        UpdateScheduleDateHeaders();
    }

    // [NEW] Helper to get start period from session
    int GetStartPeriodFromSession(ScheduleSession s)
    {
        if (s.startPeriod > 0) return s.startPeriod;
        if (string.IsNullOrEmpty(s.periods)) return 999;
        var match = System.Text.RegularExpressions.Regex.Match(s.periods, @"^\d+");
        if (match.Success) return int.Parse(match.Value);
        return 999;
    }

    VisualElement CreateSessionCard(ScheduleSession session)
    {
        // Pick color based on hash of subject code
        string colorHex = session.colorHex;
        if (string.IsNullOrEmpty(colorHex))
        {
            string[] colors = { "#fff3e0", "#e3f2fd", "#e8f5e9", "#ede7f6", "#fce4ec", "#fff8e1" };
            string[] borders = { "#ff9800", "#2196f3", "#4caf50", "#9c27b0", "#e91e63", "#ffc107" };
            int idx = Mathf.Abs(session.subjectCode?.GetHashCode() ?? 0) % colors.Length;
            colorHex = colors[idx];
        }

        var card = new VisualElement();
        card.style.backgroundColor = ColorFromHex(colorHex);
        card.style.borderTopLeftRadius = 6;
        card.style.borderTopRightRadius = 6;
        card.style.borderBottomLeftRadius = 6;
        card.style.borderBottomRightRadius = 6;
        card.style.paddingTop = 10;
        card.style.paddingBottom = 10;
        card.style.paddingLeft = 10;
        card.style.paddingRight = 10;
        card.style.marginBottom = 8;
        card.style.borderLeftWidth = 3;
        card.style.borderLeftColor = ColorFromHex("#6c5ce7");

        // Subject name
        var lblSubject = new Label($"-Môn: {session.subjectName}");
        lblSubject.style.fontSize = 11;
        lblSubject.style.color = ColorFromHex("#2d3436");
        lblSubject.style.unityFontStyleAndWeight = FontStyle.Bold;
        lblSubject.style.whiteSpace = WhiteSpace.Normal;
        card.Add(lblSubject);

        // Subject code
        if (!string.IsNullOrEmpty(session.subjectCode))
        {
            var lblCode = new Label($"({session.subjectCode})");
            lblCode.style.fontSize = 10;
            lblCode.style.color = ColorFromHex("#636e72");
            card.Add(lblCode);
        }

        // Level
        var lblLevel = new Label($"-Hệ: {session.level ?? "Đại học"}");
        lblLevel.style.fontSize = 10;
        lblLevel.style.color = ColorFromHex("#636e72");
        lblLevel.style.marginTop = 4;
        card.Add(lblLevel);

        // Class code
        if (!string.IsNullOrEmpty(session.classCode))
        {
            var lblClassTitle = new Label("-Mã lớp tín chỉ:");
            lblClassTitle.style.fontSize = 10;
            lblClassTitle.style.color = ColorFromHex("#636e72");
            card.Add(lblClassTitle);

            var lblClassCode = new Label(session.classCode);
            lblClassCode.style.fontSize = 10;
            lblClassCode.style.color = ColorFromHex("#0984e3");
            lblClassCode.style.unityFontStyleAndWeight = FontStyle.Bold;
            card.Add(lblClassCode);
        }

        // Periods
        var lblPeriods = new Label($"-Tiết: {session.periods}");
        lblPeriods.style.fontSize = 10;
        lblPeriods.style.color = ColorFromHex("#636e72");
        lblPeriods.style.marginTop = 4;
        card.Add(lblPeriods);

        // Room
        var lblRoom = new Label($"-Phòng: {session.room}");
        lblRoom.style.fontSize = 10;
        lblRoom.style.color = ColorFromHex("#d63031");
        lblRoom.style.unityFontStyleAndWeight = FontStyle.Bold;
        card.Add(lblRoom);

        return card;
    }

    void UpdateScheduleDateHeaders()
    {
        // Get Monday of current week
        DateTime today = DateTime.Now;
        int diff = (7 + (today.DayOfWeek - DayOfWeek.Monday)) % 7;
        DateTime monday = today.AddDays(-diff);

        for (int i = 0; i < 6; i++)
        {
            DateTime day = monday.AddDays(i);
            var lblDate = _uiRoot.Q<Label>($"LblDay{i + 2}Date");
            if (lblDate != null)
            {
                lblDate.text = day.ToString("dd/MM/yyyy");
            }

            // Highlight today column
            var headerCell = lblDate?.parent;
            if (headerCell != null)
            {
                bool isToday = day.Date == today.Date;
                if (isToday)
                {
                    headerCell.style.backgroundColor = ColorFromHex("#6c5ce7");
                    var lblHeader = _uiRoot.Q<Label>($"LblDay{i + 2}Header");
                    if (lblHeader != null) lblHeader.style.color = Color.white;
                    if (lblDate != null) lblDate.style.color = new Color(1, 1, 1, 0.8f);
                }
                else
                {
                    // Reset to default
                    headerCell.style.backgroundColor = StyleKeyword.Null; // Transparent/Default
                    var lblHeader = _uiRoot.Q<Label>($"LblDay{i + 2}Header");
                    if (lblHeader != null) lblHeader.style.color = ColorFromHex("#2d3436"); // Default dark Text
                    if (lblDate != null) lblDate.style.color = ColorFromHex("#636e72"); // Default grey Date
                }
            }
        }
    }

    void RenderTodaySchedule()
    {
        if (_todayScheduleContainer == null) return;

        _todayScheduleContainer.Clear();

        // Get today's sessions
        int todayDayOfWeek = (int)DateTime.Now.DayOfWeek;
        // Convert: C# Sunday=0, Monday=1... → Our format: Thứ 2=2, Thứ 3=3...
        int ourDay = todayDayOfWeek == 0 ? 8 : todayDayOfWeek + 1; // Sunday=8 (not used)

        var todaySessions = _scheduleList.Where(s => s.dayOfWeek == ourDay).ToList();

        Debug.Log($"[Dashboard] RenderToday: Date={DateTime.Now}, DayOfWeek={todayDayOfWeek}, OurDay={ourDay}, Found={todaySessions.Count}");

        if (todaySessions.Count == 0)
        {
            var lblEmpty = new Label("Không có lịch học hôm nay 🎉");
            lblEmpty.style.color = ColorFromHex("#636e72");
            lblEmpty.style.fontSize = 16;
            lblEmpty.style.paddingTop = 20;
            lblEmpty.style.paddingBottom = 20;
            lblEmpty.style.unityTextAlign = TextAnchor.MiddleCenter;
            _todayScheduleContainer.Add(lblEmpty);
            return;
        }

        foreach (var session in todaySessions)
        {
            var row = new VisualElement();
            row.AddToClassList("table-row");
            row.style.flexDirection = FlexDirection.Row;
            row.style.paddingTop = 8;
            row.style.paddingBottom = 8;
            row.style.borderBottomWidth = 1;
            row.style.borderBottomColor = ColorFromHex("#dfe6e9");

            // Time from periods
            string timeStr = ConvertPeriodsToTime(session.periods);
            var lblTime = new Label(timeStr);
            lblTime.AddToClassList("col-time");
            lblTime.style.width = new StyleLength(new Length(20, LengthUnit.Percent));
            row.Add(lblTime);

            // Subject
            var lblSubject = new Label(session.subjectName);
            lblSubject.AddToClassList("col-subject");
            lblSubject.style.width = new StyleLength(new Length(35, LengthUnit.Percent));
            row.Add(lblSubject);

            // Room
            var lblRoom = new Label(session.room);
            lblRoom.AddToClassList("col-room");
            lblRoom.style.width = new StyleLength(new Length(20, LengthUnit.Percent));
            row.Add(lblRoom);

            // Status
            var status = GetSessionStatus(session.periods);
            var lblStatus = new Label(status);
            lblStatus.style.width = new StyleLength(new Length(25, LengthUnit.Percent));
            lblStatus.style.fontSize = 13; // Increased size
            lblStatus.style.paddingTop = 4;
            lblStatus.style.paddingBottom = 4;
            lblStatus.style.paddingLeft = 8;
            lblStatus.style.paddingRight = 8;
            lblStatus.style.borderTopLeftRadius = 4;
            lblStatus.style.borderTopRightRadius = 4;
            lblStatus.style.borderBottomLeftRadius = 4;
            lblStatus.style.borderBottomRightRadius = 4;

            if (status == "Đang diễn ra")
            {
                lblStatus.style.backgroundColor = ColorFromHex("#00b894");
                lblStatus.style.color = Color.white;
            }
            else if (status == "Sắp tới")
            {
                lblStatus.style.backgroundColor = ColorFromHex("#fdcb6e");
                lblStatus.style.color = ColorFromHex("#2d3436");
            }
            else
            {
                lblStatus.style.backgroundColor = ColorFromHex("#dfe6e9");
                lblStatus.style.color = ColorFromHex("#636e72");
            }

            row.Add(lblStatus);
            _todayScheduleContainer.Add(row);
        }
    }

    string ConvertPeriodsToTime(string periods)
    {
        try
        {
            var parts = periods.Replace("->", "-").Replace(">", "-").Split('-');
            if (parts.Length >= 2)
            {
                int startPeriod = int.Parse(parts[0].Trim());
                int endPeriod = int.Parse(parts[1].Trim());

                var start = GetPeriodTime(startPeriod);
                var end = GetPeriodTime(endPeriod);

                return $"{start.startHour:D2}:{start.startMin:D2} - {end.endHour:D2}:{end.endMin:D2}";
            }
            // Single period case
            if (int.TryParse(periods, out int p))
            {
                var time = GetPeriodTime(p);
                return $"{time.startHour:D2}:{time.startMin:D2} - {time.endHour:D2}:{time.endMin:D2}";
            }
        }
        catch { }
        return periods;
    }

    string GetSessionStatus(string periods)
    {
        try
        {
            var parts = periods.Replace("->", "-").Replace(">", "-").Split('-');
            int startPeriod = 1, endPeriod = 1;

            if (parts.Length >= 2)
            {
                startPeriod = int.Parse(parts[0].Trim());
                endPeriod = int.Parse(parts[1].Trim());
            }
            else if (int.TryParse(periods, out int p))
            {
                startPeriod = endPeriod = p;
            }

            var startP = GetPeriodTime(startPeriod);
            var endP = GetPeriodTime(endPeriod);

            DateTime now = DateTime.Now;
            DateTime startTime = new DateTime(now.Year, now.Month, now.Day, startP.startHour, startP.startMin, 0);
            DateTime endTime = new DateTime(now.Year, now.Month, now.Day, endP.endHour, endP.endMin, 0);

            if (now < startTime) return "Sắp tới";
            if (now > endTime) return "Đã qua";
            return "Đang diễn ra";
        }
        catch { }
        return "Sắp tới";
    }

    // [NEW] 16 Periods Config
    (int startHour, int startMin, int endHour, int endMin) GetPeriodTime(int period)
    {
        // Periods 1-16 mapping
        switch (period)
        {
            case 1: return (7, 30, 8, 15);
            case 2: return (8, 15, 9, 00);
            case 3: return (9, 00, 9, 45);
            case 4: return (10, 00, 10, 45);
            case 5: return (10, 45, 11, 30);
            case 6: return (11, 30, 12, 15); // End morning
            case 7: return (12, 45, 13, 30);
            case 8: return (13, 30, 14, 15);
            case 9: return (14, 15, 15, 00);
            case 10: return (15, 15, 16, 00);
            case 11: return (16, 00, 16, 45);
            case 12: return (16, 45, 17, 30); // End afternoon
            case 13: return (18, 00, 18, 45);
            case 14: return (18, 45, 19, 30);
            case 15: return (19, 30, 20, 15);
            case 16: return (20, 15, 21, 00);
            default: return (7, 0, 21, 0); // Fallback
        }
    }

    // =================== SESSION-BASED ATTENDANCE (ABSOLUTE) ===================
    private const int LATE_GRACE_MINUTES = 10;      // trễ nếu check-in sau start + 10'
    private const int CHECKIN_EARLY_MINUTES = 5;    // cho phép checkin sớm 5'
    private const int CHECKIN_LATE_MINUTES = 30;    // cho phép checkin muộn 30' (vẫn tính trong buổi)

    private struct SessionOccur
    {
        public DateTime Date;
        public ScheduleSession Session;
        public DateTime Start;
        public DateTime End;
        public string Key;
    }

    private bool TryParseLogDateTime(AttendanceLog log, out DateTime dt)
    {
        dt = default;
        if (log == null) return false;
        if (string.IsNullOrWhiteSpace(log.date) || string.IsNullOrWhiteSpace(log.time)) return false;

        string s = $"{log.date} {log.time}";
        string[] fmts =
        {
            "dd/MM/yyyy HH:mm:ss", "d/M/yyyy H:mm:ss", "dd/MM/yyyy H:mm:ss", "d/M/yyyy HH:mm:ss",
            "dd/MM/yyyy HH:mm",    "d/M/yyyy H:mm",    "dd/MM/yyyy H:mm",    "d/M/yyyy HH:mm"
        };

        return DateTime.TryParseExact(
            s, fmts,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out dt
        );
    }

    private List<SessionOccur> BuildOccurrences(DateTime startDate, DateTime endDate)
    {
        var res = new List<SessionOccur>();
        if (_scheduleList == null || _scheduleList.Count == 0) return res;

        for (var d = startDate.Date; d <= endDate.Date; d = d.AddDays(1))
        {
            int dow = (int)d.DayOfWeek;
            if (dow == 0) dow = 7; // Sunday -> 7

            foreach (var s in _scheduleList)
            {
                if (s.dayOfWeek != dow) continue;

                var startPT = GetPeriodTime(s.startPeriod);
                var endPT = GetPeriodTime(s.endPeriod);

                var st = new DateTime(d.Year, d.Month, d.Day, startPT.startHour, startPT.startMin, 0);
                var en = new DateTime(d.Year, d.Month, d.Day, endPT.endHour, endPT.endMin, 59);

                res.Add(new SessionOccur
                {
                    Date = d,
                    Session = s,
                    Start = st,
                    End = en,
                    Key = $"{d:yyyyMMdd}_{s.dayOfWeek}_{s.startPeriod}_{s.endPeriod}"
                });
            }
        }

        res.Sort((a, b) => a.Start.CompareTo(b.Start));
        return res;
    }

    // Lấy buổi để hiển thị ở Trang chủ
    private SessionOccur? FindHomeSessionOccurrence(DateTime today, DateTime now)
    {
        var occs = BuildOccurrences(today, today);
        if (occs.Count == 0) return null;

        // 1) buổi đang diễn ra
        foreach (var occ in occs)
        {
            var winStart = occ.Start.AddMinutes(-CHECKIN_EARLY_MINUTES);
            var winEnd = occ.End.AddMinutes(CHECKIN_LATE_MINUTES);
            if (now >= winStart && now <= winEnd) return occ;
        }

        // 2) buổi gần nhất đã kết thúc
        SessionOccur? last = null;
        foreach (var occ in occs)
            if (occ.End <= now) last = occ;

        return last;
    }

    // map MSSV -> thời điểm check-in sớm nhất trong buổi
    private Dictionary<string, DateTime> GetEarliestCheckinByStudent(SessionOccur occ)
    {
        var dict = new Dictionary<string, DateTime>();
        if (_allLogs == null || _allLogs.Count == 0) return dict;

        var winStart = occ.Start.AddMinutes(-CHECKIN_EARLY_MINUTES);
        var winEnd = occ.End.AddMinutes(CHECKIN_LATE_MINUTES);

        foreach (var log in _allLogs)
        {
            if (string.IsNullOrWhiteSpace(log.mssv)) continue;
            if (!TryParseLogDateTime(log, out var dt)) continue;
            if (dt < winStart || dt > winEnd) continue;

            if (!dict.TryGetValue(log.mssv, out var cur) || dt < cur)
                dict[log.mssv] = dt;
        }

        return dict;
    }

    void OnImportScheduleClick()
    {
        Debug.Log("[ScheduleImport] OnImportScheduleClick triggered");
        // Use Windows file dialog via P/Invoke
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        // [FIX] Remove trailing \0 because FilePicker adds it
        string path = UI.FilePicker.OpenFile("Import Lịch Học (Excel)", "Excel Files (*.xlsx;*.xls)\0*.xlsx;*.xls", "xlsx");
        Debug.Log($"[ScheduleImport] FilePicker returned path: '{path}'");
        if (!string.IsNullOrEmpty(path))
        {
            Debug.Log("[ScheduleImport] Starting ImportScheduleFromExcel coroutine...");
            StartCoroutine(ImportScheduleFromExcel(path));
        }
#else
        ShowToast("📂 Import chỉ hỗ trợ trên Windows PC", colOrange);
#endif
    }

    // [FIX] Improved Import Logic with Verbose Errors
    IEnumerator ImportScheduleFromExcel(string path)
    {
        ShowToast("⏳ Đang phân tích file... (vui lòng chờ)", colBlue);
        yield return null;

        if (!File.Exists(path)) { ShowToast("❌ File không tồn tại!", colRed); yield break; }

        List<ScheduleSession> newSessions = new List<ScheduleSession>();
        bool success = false;
        string errorMsg = "";

        // [DEBUG] Run synchronously on main thread to find the issue
        Debug.Log("[ScheduleImport] Starting Excel parsing (synchronous)...");
        try
        {
            Debug.Log("[ScheduleImport] Opening file: " + path);
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                Debug.Log("[ScheduleImport] File stream opened successfully. Creating ExcelReader...");
                EnsureExcelEncoding(); // ✅ FIX: Register CodePages encoding
                using (var reader = ExcelReaderFactory.CreateReader(stream))
                {
                    Debug.Log("[ScheduleImport] ExcelReader created. Reading as DataSet...");
                    var result = reader.AsDataSet();
                    if (result.Tables.Count == 0)
                    {
                        errorMsg = "File Excel rỗng!";
                        yield break;
                    }
                    var table = result.Tables[0];
                    Debug.Log($"[ScheduleImport] Table has {table.Rows.Count} rows, {table.Columns.Count} columns");

                    // Debug: Print first 5 rows to understand format
                    for (int dbgR = 0; dbgR < Math.Min(5, table.Rows.Count); dbgR++)
                    {
                        string rowContent = $"Row {dbgR}: ";
                        for (int dbgC = 0; dbgC < Math.Min(10, table.Columns.Count); dbgC++)
                        {
                            rowContent += $"[{table.Rows[dbgR][dbgC]}] ";
                        }
                        Debug.Log("[ScheduleImport] " + rowContent);
                    }

                    // 1. Scan Header (Thứ 2...)
                    int headerRow = -1;
                    Dictionary<int, int> colToDay = new Dictionary<int, int>();

                    // Scan first 30 rows
                    for (int r = 0; r < Math.Min(30, table.Rows.Count); r++)
                    {
                        for (int c = 0; c < table.Columns.Count; c++)
                        {
                            string cell = table.Rows[r][c]?.ToString()?.ToLower() ?? "";
                            if (cell.Contains("thứ 2") || cell.Contains("monday") || cell.Contains("thứ hai"))
                            {
                                headerRow = r;
                                Debug.Log($"[ScheduleImport] Found header at Row {r}, Col {c}: '{cell}'");
                                break;
                            }
                        }
                        if (headerRow != -1) break;
                    }

                    Debug.Log($"[ScheduleImport] Header row result: {headerRow}");

                    if (headerRow != -1)
                    {
                        // Map Columns
                        for (int c = 0; c < table.Columns.Count; c++)
                        {
                            string h = table.Rows[headerRow][c]?.ToString()?.ToLower() ?? "";
                            if (h.Contains("thứ 2") || h.Contains("hai")) colToDay[c] = 2;
                            else if (h.Contains("thứ 3") || h.Contains("ba")) colToDay[c] = 3;
                            else if (h.Contains("thứ 4") || h.Contains("tư") || h.Contains("bốn")) colToDay[c] = 4;
                            else if (h.Contains("thứ 5") || h.Contains("năm")) colToDay[c] = 5;
                            else if (h.Contains("thứ 6") || h.Contains("sáu")) colToDay[c] = 6;
                            else if (h.Contains("thứ 7") || h.Contains("bảy")) colToDay[c] = 7;
                            else if (h.Contains("chủ nhật") || h.Contains("cn") || h.Contains("sunday")) colToDay[c] = 8;
                        }
                    }
                    else
                    {
                        errorMsg = "Không tìm thấy hàng tiêu đề (Thứ 2...)";
                        yield break;
                    }

                    // 2. Parse Data - NEW VERTICAL FORMAT
                    // Data is spread across ROWS: -Môn: on row N, -Tiết: on row N+3, -Phòng: on row N+4, etc.
                    // Strategy: For each day column, scan down rows and collect sessions

                    foreach (var kvp in colToDay)
                    {
                        int col = kvp.Key;
                        int dayOfWeek = kvp.Value;

                        // Collect all cells for this column (skip header rows)
                        string currentSubject = "";
                        string currentPeriods = "";
                        string currentRoom = "";
                        string currentClassCode = "";
                        bool inSession = false;

                        for (int row = headerRow + 2; row < table.Rows.Count; row++) // +2 to skip date row
                        {
                            string cell = table.Rows[row][col]?.ToString()?.Trim() ?? "";
                            if (string.IsNullOrEmpty(cell)) continue;

                            // Parse cell prefix
                            if (cell.StartsWith("-Môn:") || cell.StartsWith("Môn:"))
                            {
                                // If we were in a session and have data, save it first
                                if (inSession && !string.IsNullOrEmpty(currentSubject) && !string.IsNullOrEmpty(currentPeriods))
                                {
                                    SaveSession(newSessions, currentSubject, currentPeriods, currentRoom, currentClassCode, dayOfWeek);
                                }
                                // Start new session
                                currentSubject = cell.Replace("-Môn:", "").Replace("Môn:", "").Trim();
                                currentPeriods = "";
                                currentRoom = "";
                                currentClassCode = "";
                                inSession = true;
                            }
                            else if (inSession)
                            {
                                if (cell.StartsWith("-Tiết:") || cell.StartsWith("Tiết:"))
                                    currentPeriods = cell.Replace("-Tiết:", "").Replace("Tiết:", "").Trim();
                                else if (cell.StartsWith("-Phòng:") || cell.StartsWith("Phòng:"))
                                    currentRoom = cell.Replace("-Phòng:", "").Replace("Phòng:", "").Trim();
                                else if (cell.StartsWith("-Mã lớp") || cell.StartsWith("Mã lớp"))
                                    currentClassCode = cell.Substring(cell.IndexOf(':') + 1).Trim();
                            }
                        }
                        // Save last session in column
                        if (inSession && !string.IsNullOrEmpty(currentSubject) && !string.IsNullOrEmpty(currentPeriods))
                        {
                            SaveSession(newSessions, currentSubject, currentPeriods, currentRoom, currentClassCode, dayOfWeek);
                        }
                    }

                    if (newSessions.Count > 0) success = true;
                    else errorMsg = "Không tìm thấy lớp học nào (cần format: -Môn:... -Tiết:...)";
                }
            }
        }
        catch (Exception ex)
        {
            errorMsg = "Lỗi đọc file: " + ex.Message;
            Debug.LogError("[ScheduleImport] Exception: " + ex.ToString());
        }

        Debug.Log("[ScheduleImport] Parsing completed. Success: " + success + ", Sessions: " + newSessions.Count);

        if (success && newSessions.Count > 0)
        {
            // Assign Random Colors on Main Thread
            foreach (var s in newSessions) s.colorHex = "#" + ColorUtility.ToHtmlStringRGB(GetRandomPastelColor());

            _scheduleList.Clear();
            _scheduleList.AddRange(newSessions);
            SaveScheduleToLocal();
            StartCoroutine(PushScheduleBundleToServer(silent: true)); // Sync lên server
            RenderScheduleTab();
            RenderTodaySchedule();
            ShowToast($"✅ Đã import {newSessions.Count} buổi học!", colGreen);

            // [FIX] Update Stats immediately
            UpdateStatsFromLogs();
        }
        else
        {
            if (!string.IsNullOrEmpty(errorMsg))
            {
                GUIUtility.systemCopyBuffer = errorMsg;
                ShowToast($"Import lỗi: {errorMsg}", colOrange);
            }
            else
            {
                ShowToast("⚠️ Không tìm thấy dữ liệu hợp lệ (Cần 'Môn:' và 'Tiết:')", colOrange);
            }
            // Ensure UI is refreshed even on error
            RenderScheduleTab();
        }
    }

    private Color GetRandomPastelColor()
    {
        return Color.HSVToRGB(UnityEngine.Random.value, 0.25f, 0.95f);
    }

    // [NEW] Helper to save a parsed session
    private void SaveSession(List<ScheduleSession> list, string subject, string periods, string room, string classCode, int dayOfWeek)
    {
        var sess = new ScheduleSession();
        sess.id = System.Guid.NewGuid().ToString();
        sess.subjectName = subject;
        sess.classCode = classCode;
        sess.periods = periods;

        // Parse start/end periods from "1->5" or "7->11"
        int startP = 1, endP = 1;
        var parts = periods.Split(new char[] { '-', '–', '>' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            int.TryParse(parts[0].Trim(), out startP);
            int.TryParse(parts[parts.Length - 1].Trim(), out endP);
        }
        else if (parts.Length == 1)
        {
            int.TryParse(parts[0].Trim(), out startP);
            endP = startP;
        }
        sess.startPeriod = startP;
        sess.endPeriod = endP;

        sess.room = !string.IsNullOrEmpty(room) ? room : "N/A";
        sess.dayOfWeek = dayOfWeek;
        if (sess.dayOfWeek == 8) sess.dayOfWeek = 0; // Sunday

        Debug.Log($"[ScheduleImport] SAVED: Day={sess.dayOfWeek}, Subject={sess.subjectName}, Periods={periods}, Room={sess.room}");
        list.Add(sess);
    }


    void OnAddSessionClick()
    {
        if (modalAddSessionTemplate == null)
        {
            ShowToast("⚠️ Thiếu template ModalAddSession", colOrange);
            return;
        }

        // Instantiate modal
        var modal = modalAddSessionTemplate.CloneTree();
        modal.style.flexGrow = 1;
        modal.style.width = Length.Percent(100);
        modal.style.height = Length.Percent(100);
        modal.pickingMode = PickingMode.Ignore; // [FIX] Ensure container ignores clicks (let children handle)
        _modalContainer.Clear();
        _modalContainer.Add(modal);
        _modalContainer.style.display = DisplayStyle.Flex;
        _modalContainer.pickingMode = PickingMode.Ignore;

        // Get form elements
        var overlay = modal.Q<VisualElement>("ModalOverlay");
        if (overlay != null)
        {
            overlay.pickingMode = PickingMode.Position;
            // Opacity animation for overlay
            overlay.style.opacity = 0;
            overlay.schedule.Execute(() => overlay.style.opacity = 1).ExecuteLater(10);
        }

        // [FIX] Bind Events FIRST to ensure functionality
        var btnClose = modal.Q<Button>("BtnCloseModal");
        var btnCancel = modal.Q<Button>("BtnCancelSession");
        var btnSave = modal.Q<Button>("BtnSaveSession");

        // Force buttons to be clickable
        if (btnSave != null) btnSave.pickingMode = PickingMode.Position;
        if (btnClose != null) btnClose.pickingMode = PickingMode.Position;
        if (btnCancel != null) btnCancel.pickingMode = PickingMode.Position;

        System.Action closeModal = () =>
        {
            _modalContainer.Clear();
            _modalContainer.style.display = DisplayStyle.None;
        };

        if (btnClose != null) btnClose.clicked += closeModal;
        if (btnCancel != null) btnCancel.clicked += closeModal;

        if (btnSave != null) btnSave.clicked += () =>
        {
            Debug.Log("[Dashboard] Save Session Clicked"); // DEBUG LOG

            // Get form values
            var inpSubject = modal.Q<TextField>("InpSubjectName");
            var inpCode = modal.Q<TextField>("InpSubjectCode");
            var inpClass = modal.Q<TextField>("InpClassCode");
            var inpDay = modal.Q<TextField>("InpDayOfWeek");
            var inpLevel = modal.Q<TextField>("InpLevel");
            var inpPeriods = modal.Q<TextField>("InpPeriods");
            var inpRoom = modal.Q<TextField>("InpRoom");

            string subject = "";
            string periods = "";
            string room = "";

            try
            {
                // Safe read with trim
                subject = inpSubject?.value?.Trim() ?? "";
                periods = inpPeriods?.value?.Trim() ?? "";
                room = inpRoom?.value?.Trim() ?? "";
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Dashboard] Input Error: {ex}");
                return;
            }

            Debug.Log($"[Dashboard] Input: Subj='{subject}', Per='{periods}', Room='{room}'");

            if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(periods) || string.IsNullOrEmpty(room))
            {
                ShowToast("⚠️ Vui lòng điền đầy đủ các trường bắt buộc (*)", colOrange);
                return;
            }

            // Parse day of week from TextField (2-7)
            int dayOfWeek = 2;
            if (int.TryParse(inpDay?.value, out int parsedDay) && parsedDay >= 2 && parsedDay <= 7)
            {
                dayOfWeek = parsedDay;
            }

            var newSession = new ScheduleSession
            {
                subjectName = subject,
                subjectCode = inpCode?.value ?? "",
                classCode = inpClass?.value ?? "",
                level = inpLevel?.value ?? "Đại học",
                dayOfWeek = dayOfWeek,
                periods = periods,
                room = room
            };

            _scheduleList.Add(newSession);
            SaveScheduleToLocal();
            StartCoroutine(PushScheduleBundleToServer(silent: true)); // Sync lên server
            RenderScheduleTab();
            RenderTodaySchedule();

            closeModal();
            ShowToast($"✅ Đã thêm buổi học: {subject}", colGreen);
        };

        // [FIX] Run animation last, wrap in try-catch to prevent breaking logic
        var modalCard = modal.Q<VisualElement>("ModalCard");
        if (modalCard != null)
        {
            try
            {
                AnimateEntry(modalCard);
            }
            catch { /* Ignore animation error */ }
        }
    }

    void OnClearScheduleClick()
    {
        if (_scheduleList.Count == 0)
        {
            ShowToast("📋 Không có dữ liệu để xóa", colOrange);
            return;
        }

        // Clear all
        _scheduleList.Clear();
        SaveScheduleToLocal();
        StartCoroutine(PushScheduleBundleToServer(silent: true)); // Sync lên server
        RenderScheduleTab();
        RenderTodaySchedule();

        ShowToast("🗑️ Đã xóa tất cả lịch học", colRed);
    }

    // ============================================================
    // LOGIN LOGIC
    // ============================================================

    void SetupLoginUI()
    {
        _loginOverlay = _uiRoot.Q<VisualElement>("LoginOverlay");
        _inpLoginUser = _uiRoot.Q<TextField>("InpLoginUser");
        _inpLoginPass = _uiRoot.Q<TextField>("InpLoginPass");
        _inpLoginEmail = _uiRoot.Q<TextField>("InpLoginEmail"); // [NEW]

        ClearLoginInputs(); // [FIX] Ensure inputs are empty on startup

        _btnLoginInfo = _uiRoot.Q<Button>("BtnLoginInfo");
        _lblRegister = _uiRoot.Q<Label>("LblRegister");

        // Find Title Label using class or name (Assuming it's inside LoginCard)
        // Adjust hierarchy search if needed. For now assuming "LblLoginTitle" or generic binding.
        // If not found, we skip updating title.

        // Force Show Login if not logged in
        if (!_isLoggedIn && _loginOverlay != null)
        {
            _loginOverlay.style.display = DisplayStyle.Flex;
            // [NEW] Trigger entry animation
            AnimateLoginScreen();
        }
        else if (_loginOverlay != null)
        {
            _loginOverlay.style.display = DisplayStyle.None;
        }

        // [NEW] Setup custom logo
        var logoContainer = _uiRoot.Q<VisualElement>("ContainerLogo");
        if (logoContainer != null && schoolLogo != null)
        {
            logoContainer.style.backgroundImage = new StyleBackground(schoolLogo);
            logoContainer.style.backgroundColor = Color.white;
            logoContainer.Clear(); // Remove placeholder "UTE" text
        }

        if (_btnLoginInfo != null)
        {
            _btnLoginInfo.clicked += OnLoginClick;
        }

        var lblForgot = _uiRoot.Q<Label>("LblForgotPassword");
        if (lblForgot != null) lblForgot.RegisterCallback<ClickEvent>(ev => OnClickForgotPassword());

        if (_lblRegister != null) _lblRegister.RegisterCallback<ClickEvent>(ev => ToggleRegisterMode());

        // [NEW] Logout button
        var btnLogout = _uiRoot.Q<Button>("BtnLogout");
        if (btnLogout != null) btnLogout.clicked += OnLogoutClick;

        // [ENABLED] Auto-login: if TeacherID saved from previous session, auto-login
        if (PlayerPrefs.HasKey("TeacherID"))
        {
            string saved = PlayerPrefs.GetString("TeacherID", "");
            if (!string.IsNullOrEmpty(saved))
            {
                _currentTeacherId = saved;
                _isLoggedIn = true;

                if (_loginOverlay != null)
                    _loginOverlay.style.display = DisplayStyle.None;

                // Initialize dashboard like a normal login
                LoadScheduleFromLocal();
                EnsureDefaultClassSelected();
                UpdateClassLabels();
                LoadHiddenLogIds();
                LoadLogsLocal();
                LoadStudentDataListFromFile();
                UpdateTopStats();
                RefreshMainContent();
                UpdateStatsFromLogs();
                UpdateTrendChart();
                UpdateStatsTab();
                FetchLogsFromFirestore();
                InitDashboardStats();
                UpdateOpenGSheetButton(); // [NEW] Restore button state
                RenderScheduleTab();
                RenderTodaySchedule();
                StartCoroutine(PullScheduleBundleFromServer(autoSwitchTenant: true, silent: true));
            }
        }
    }

    void AnimateLoginScreen()
    {
        if (_loginOverlay == null) return;

        // Get elements
        var logoContainer = _loginOverlay.Q<VisualElement>("ContainerLogo");
        var headerBrand = logoContainer?.parent;

        // Get login card - it's the second direct child of LoginOverlay (after header)
        var children = _loginOverlay.Children().ToList();
        VisualElement loginCard = children.Count > 1 ? children[1] : null;

        // === ANIMATE LOGO: Pop in ===
        if (logoContainer != null)
        {
            logoContainer.style.opacity = 0;
            logoContainer.transform.scale = Vector3.one * 0.3f;

            _loginOverlay.schedule.Execute(() =>
            {
                logoContainer.style.opacity = 1;
                logoContainer.transform.scale = Vector3.one;
            }).StartingIn(100);
        }

        // === ANIMATE HEADER LABELS ===
        if (headerBrand != null)
        {
            var labels = headerBrand.Query<Label>().ToList();
            for (int i = 0; i < labels.Count; i++)
            {
                var lbl = labels[i];
                lbl.style.opacity = 0;

                int delayMs = 200 + (i * 150);
                _loginOverlay.schedule.Execute(() =>
                {
                    lbl.style.opacity = 1;
                }).StartingIn(delayMs);
            }
        }

        // === ANIMATE LOGIN CARD: Fade in + Slide up ===
        if (loginCard != null)
        {
            loginCard.style.opacity = 0;
            loginCard.transform.position = new Vector3(0, 30, 0);

            _loginOverlay.schedule.Execute(() =>
            {
                loginCard.style.opacity = 1;
                loginCard.transform.position = Vector3.zero;
            }).StartingIn(400);
        }

        // Add USS transition classes
        AddLoginTransitionStyles();
    }

    void AddLoginTransitionStyles()
    {
        // Add inline transition via schedule (USS fallback)
        var logoContainer = _loginOverlay.Q<VisualElement>("ContainerLogo");
        var headerBrand = logoContainer?.parent;
        var children = _loginOverlay.Children().ToList();
        VisualElement loginCard = children.Count > 1 ? children[1] : null;

        if (logoContainer != null)
        {
            logoContainer.style.transitionProperty = new StyleList<StylePropertyName>(
                new List<StylePropertyName> { new StylePropertyName("opacity"), new StylePropertyName("scale") }
            );
            logoContainer.style.transitionDuration = new StyleList<TimeValue>(
                new List<TimeValue> { new TimeValue(500, TimeUnit.Millisecond) }
            );
        }

        if (loginCard != null)
        {
            loginCard.style.transitionProperty = new StyleList<StylePropertyName>(
                new List<StylePropertyName> { new StylePropertyName("opacity"), new StylePropertyName("translate") }
            );
            loginCard.style.transitionDuration = new StyleList<TimeValue>(
                new List<TimeValue> { new TimeValue(600, TimeUnit.Millisecond) }
            );
        }

        if (headerBrand != null)
        {
            foreach (var lbl in headerBrand.Query<Label>().ToList())
            {
                lbl.style.transitionProperty = new StyleList<StylePropertyName>(
                    new List<StylePropertyName> { new StylePropertyName("opacity") }
                );
                lbl.style.transitionDuration = new StyleList<TimeValue>(
                    new List<TimeValue> { new TimeValue(400, TimeUnit.Millisecond) }
                );
            }
        }
    }

    void ToggleRegisterMode()
    {
        _isRegisterMode = !_isRegisterMode;
        if (_btnLoginInfo != null) _btnLoginInfo.text = _isRegisterMode ? "Đăng ký" : "Đăng nhập";

        if (_lblRegister != null) _lblRegister.text = _isRegisterMode ? "Quay lại Đăng nhập" : "Đăng ký tài khoản";

        // [FIX] Update header text on tab switch
        var lblLoginHeader = _uiRoot.Q<Label>("LblLoginHeader");
        if (lblLoginHeader != null) lblLoginHeader.text = _isRegisterMode ? "Đăng ký" : "Đăng nhập";

        // Toggle Email Field
        var emailContainer = _uiRoot.Q<VisualElement>("ContainerEmail");
        if (emailContainer != null)
        {
            emailContainer.style.display = _isRegisterMode ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // [FIX] Clear and focus password field
        if (_inpLoginPass != null) { _inpLoginPass.value = ""; _inpLoginPass.Focus(); }
    }

    void OnLogoutClick()
    {
        _isLoggedIn = false;
        _currentTeacherId = "";
        PlayerPrefs.DeleteKey("TeacherID"); // Clear saved session

        // [NEW] Clear local data
        _allLogs.Clear();
        _studentDataList.Clear();
        _scheduleList.Clear();

        // Refresh UI to empty state
        RenderScheduleTab();
        RenderTodaySchedule();
        RefreshMainContent();

        if (_loginOverlay != null) _loginOverlay.style.display = DisplayStyle.Flex;

        // [FIX] Reset to Login Mode directly (not Register)
        _isRegisterMode = false;
        if (_btnLoginInfo != null) _btnLoginInfo.text = "Đăng nhập";
        if (_lblRegister != null) _lblRegister.text = "Đăng ký tài khoản";
        var emailContainer = _uiRoot.Q<VisualElement>("ContainerEmail");
        if (emailContainer != null) emailContainer.style.display = DisplayStyle.None;
        var lblLoginHeader = _uiRoot.Q<Label>("LblLoginHeader");
        if (lblLoginHeader != null) lblLoginHeader.text = "Đăng nhập";

        ShowToast("👋 Đã đăng xuất!", colBlue);
        ClearLoginInputs();
    }

    // [NEW] Helper to clear login form
    private void ClearLoginInputs()
    {
        if (_inpLoginUser != null) _inpLoginUser.value = "";
        if (_inpLoginPass != null) _inpLoginPass.value = "";
        if (_inpLoginEmail != null) _inpLoginEmail.value = ""; // Also clear email
    }

    void OnLoginClick()
    {
        string u = _inpLoginUser?.value?.Trim();
        string p = _inpLoginPass?.value;

        if (string.IsNullOrEmpty(u) || string.IsNullOrEmpty(p))
        {
            ShowToast("⚠️ Vui lòng nhập Tên đăng nhập và Mật khẩu", colOrange);
            return;
        }

        if (_isRegisterMode)
        {
            StartCoroutine(PostAuthRegister(u, p));
        }
        else
        {
            StartCoroutine(PostAuthLogin(u, p));
        }
    }

    // --- AUTH API COROUTINES ---

    [Serializable]
    public class AuthReq
    {
        public string username;
        public string password;
        public string email; // [NEW]
    }

    [Serializable]
    public class AuthRes
    {
        public string status;
        public string message;
        public string teacher_id;
        public string error;
    }

    [Serializable]
    public class ForgotPassReq
    {
        public string email;
    }

    [Serializable]
    public class ResetPassReq
    {
        public string email;
        public string otp;
        public string new_password;
    }

    IEnumerator PostAuthLogin(string username, string password)
    {
        _btnLoginInfo.SetEnabled(false);
        string url = cloudRunApiUrl.TrimEnd('/') + "/auth/login";

        var reqData = new AuthReq { username = username, password = password };
        string json = JsonUtility.ToJson(reqData);

        var request = new UnityWebRequest(url, "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        _btnLoginInfo.SetEnabled(true);

        if (request.result != UnityWebRequest.Result.Success)
        {
            ShowToast($"❌ Đăng nhập thất bại: {request.error}", colRed);
            Debug.LogError(request.downloadHandler.text);
            // [FIX] Clear and focus password field so user can retry
            if (_inpLoginPass != null) { _inpLoginPass.value = ""; _inpLoginPass.Focus(); }
        }
        else
        {
            try
            {
                var res = JsonUtility.FromJson<AuthRes>(request.downloadHandler.text);
                if (!string.IsNullOrEmpty(res.teacher_id))
                {
                    _currentTeacherId = res.teacher_id;
                    _isLoggedIn = true;

                    // Save local session for auto-login on next app launch
                    PlayerPrefs.SetString("TeacherID", _currentTeacherId);
                    PlayerPrefs.Save();

                    ShowToast($"✅ Xin chào, {_currentTeacherId}!", colGreen);
                    if (_loginOverlay != null) _loginOverlay.style.display = DisplayStyle.None;

                    // Refresh Data for new user
                    // Clear old data to avoid mixing
                    _allLogs.Clear();
                    _studentDataList.Clear();
                    _hiddenLogIds.Clear();

                    // 1) Restore context (term/class) first so local file keys are correct
                    LoadScheduleFromLocal();
                    EnsureDefaultClassSelected();
                    UpdateClassLabels();

                    // 2) Load local cache immediately so Home shows previous data right away
                    LoadHiddenLogIds();
                    LoadLogsLocal();
                    LoadStudentDataListFromFile();

                    // Refresh Home UI from cache
                    UpdateTopStats();
                    RefreshMainContent();
                    UpdateStatsFromLogs();
                    UpdateTrendChart();
                    UpdateStatsTab();

                    // 3) Now sync from server (this will refresh Home again when done)
                    FetchLogsFromFirestore();

                    // 4) Init dashboard widgets (week/day dropdown + profiles cache)
                    InitDashboardStats();

                    // 5) Schedule UI
                    RenderScheduleTab();
                    RenderTodaySchedule();

                    // 6) Sync schedule bundle from server (lịch + học kỳ + lớp từ máy khác)
                    StartCoroutine(PullScheduleBundleFromServer(autoSwitchTenant: true, silent: true));
                }
                else
                {
                    ShowToast($"❌ Lỗi: {res.message ?? "Unknown"}", colRed);
                }
            }
            catch (Exception e)
            {
                ShowToast($"❌ Lỗi xử lý: {e.Message}", colRed);
            }
        }
    }

    // --- FORGOT PASSWORD LOGIC ---

    void OnClickForgotPassword()
    {
        var modal = _uiRoot.Q<VisualElement>("ModalForgotPassword");
        if (modal == null)
        {
            ShowToast("❌ Không tìm thấy giao diện Quên mật khẩu!", colRed);
            return;
        }

        modal.style.display = DisplayStyle.Flex;

        // Reset Steps
        var step1 = modal.Q<VisualElement>("StepSendOtp");
        var step2 = modal.Q<VisualElement>("StepResetPass");
        if (step1 != null) step1.style.display = DisplayStyle.Flex;
        if (step2 != null) step2.style.display = DisplayStyle.None;

        var inpEmail = modal.Q<TextField>("InpForgotEmail");
        var inpOtp = modal.Q<TextField>("InpForgotOtp");
        var inpNewPass = modal.Q<TextField>("InpForgotNewPass");

        if (inpEmail != null) inpEmail.value = "";
        if (inpOtp != null) inpOtp.value = "";
        if (inpNewPass != null) inpNewPass.value = "";

        // Bind Buttons
        var btnSend = modal.Q<Button>("BtnSendOtp");
        var btnReset = modal.Q<Button>("BtnConfirmReset");
        var btnClose = modal.Q<Button>("BtnCloseForgot");

        // Simple closure binding (Note: Avoid multiple bindings if possible, but safeish if modal recreated or careful)
        // Better: Remove old listeners if possible. 
        if (btnClose != null)
        {
            btnClose.clicked -= OnCloseForgotClicked;
            btnClose.clicked += OnCloseForgotClicked;
        }

        if (btnSend != null)
        {
            btnSend.clicked -= OnSendOtpClicked;
            btnSend.clicked += OnSendOtpClicked;
        }

        if (btnReset != null)
        {
            btnReset.clicked -= OnResetPassClicked;
            btnReset.clicked += OnResetPassClicked;
        }
    }

    void OnCloseForgotClicked()
    {
        var modal = _uiRoot.Q<VisualElement>("ModalForgotPassword");
        if (modal != null) modal.style.display = DisplayStyle.None;
    }

    void OnSendOtpClicked()
    {
        var modal = _uiRoot.Q<VisualElement>("ModalForgotPassword");
        var inpEmail = modal.Q<TextField>("InpForgotEmail");
        string email = inpEmail?.value?.Trim();

        if (string.IsNullOrEmpty(email)) { ShowToast("⚠️ Vui lòng nhập Email!", colOrange); return; }

        StartCoroutine(PostSendOtp(email));
    }

    void OnResetPassClicked()
    {
        var modal = _uiRoot.Q<VisualElement>("ModalForgotPassword");
        var inpEmail = modal.Q<TextField>("InpForgotEmail"); // Read from step 1
        var inpOtp = modal.Q<TextField>("InpForgotOtp");
        var inpNewPass = modal.Q<TextField>("InpForgotNewPass");

        string email = inpEmail?.value?.Trim();
        string otp = inpOtp?.value?.Trim();
        string newPass = inpNewPass?.value;

        if (string.IsNullOrEmpty(otp) || string.IsNullOrEmpty(newPass)) { ShowToast("⚠️ Vui lòng nhập đủ thông tin!", colOrange); return; }

        StartCoroutine(PostResetPassword(email, otp, newPass));
    }

    IEnumerator PostSendOtp(string email)
    {
        ShowToast("⏳ Đang gửi mã OTP...", colBlue);
        string url = cloudRunApiUrl.TrimEnd('/') + "/auth/forgot-password";
        var req = new ForgotPassReq { email = email };
        string json = JsonUtility.ToJson(req);

        var request = new UnityWebRequest(url, "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            ShowToast("✅ Đã gửi mã OTP! Kiểm tra Email.", colGreen);
            // Switch to Step 2
            var modal = _uiRoot.Q<VisualElement>("ModalForgotPassword");
            var step1 = modal.Q<VisualElement>("StepSendOtp");
            var step2 = modal.Q<VisualElement>("StepResetPass");
            if (step1 != null) step1.style.display = DisplayStyle.None;
            if (step2 != null) step2.style.display = DisplayStyle.Flex;
        }
        else
        {
            ShowToast($"❌ Lỗi gửi OTP: {request.error}", colRed);
            Debug.LogError(request.downloadHandler.text);
        }
    }

    IEnumerator PostResetPassword(string email, string otp, string newPass)
    {
        ShowToast("⏳ Đang đổi mật khẩu...", colBlue);
        string url = cloudRunApiUrl.TrimEnd('/') + "/auth/reset-password";
        var req = new ResetPassReq { email = email, otp = otp, new_password = newPass };
        string json = JsonUtility.ToJson(req);

        var request = new UnityWebRequest(url, "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            ShowToast("✅ Đổi mật khẩu thành công! Vui lòng đăng nhập.", colGreen);
            var modal = _uiRoot.Q<VisualElement>("ModalForgotPassword");
            if (modal != null) modal.style.display = DisplayStyle.None;
        }
        else
        {
            ShowToast($"❌ Lỗi đổi pass: {request.downloadHandler.text}", colRed);
        }
    }

    // --- CHANGE PASSWORD LOGIC ---
    void OnClickChangePassword()
    {
        var modal = _uiRoot.Q<VisualElement>("ModalChangePassword");
        if (modal != null) modal.style.display = DisplayStyle.Flex;

        // Reset Inputs
        var inpOld = modal.Q<TextField>("InpOldPass");
        var inpNew = modal.Q<TextField>("InpNewPass");
        var inpConfirm = modal.Q<TextField>("InpConfirmNewPass");
        if (inpOld != null) inpOld.value = "";
        if (inpNew != null) inpNew.value = "";
        if (inpConfirm != null) inpConfirm.value = "";

        // Bind
        var btnSubmit = modal.Q<Button>("BtnSubmitChangePass");
        var btnClose = modal.Q<Button>("BtnCloseChangePass");

        if (btnClose != null)
        {
            btnClose.clicked -= OnCloseChangePass;
            btnClose.clicked += OnCloseChangePass;
        }
        if (btnSubmit != null)
        {
            btnSubmit.clicked -= OnSubmitChangePass;
            btnSubmit.clicked += OnSubmitChangePass;
        }
    }

    void OnCloseChangePass()
    {
        var modal = _uiRoot.Q<VisualElement>("ModalChangePassword");
        if (modal != null) modal.style.display = DisplayStyle.None;
    }

    void OnSubmitChangePass()
    {
        var modal = _uiRoot.Q<VisualElement>("ModalChangePassword");
        var inpOld = modal.Q<TextField>("InpOldPass");
        var inpNew = modal.Q<TextField>("InpNewPass");
        var inpConfirm = modal.Q<TextField>("InpConfirmNewPass");

        string oldPass = inpOld?.value;
        string newPass = inpNew?.value;
        string confirmPass = inpConfirm?.value;

        if (string.IsNullOrEmpty(oldPass) || string.IsNullOrEmpty(newPass))
        {
            ShowToast("⚠️ Vui lòng nhập đủ thông tin!", colOrange);
            return;
        }

        if (newPass != confirmPass)
        {
            ShowToast("⚠️ Mật khẩu xác nhận không khớp!", colRed);
            return;
        }

        StartCoroutine(PostChangePassword(oldPass, newPass));
    }

    IEnumerator PostChangePassword(string oldPass, string newPass)
    {
        ShowToast("⏳ Đang đổi mật khẩu...", colBlue);
        string url = cloudRunApiUrl.TrimEnd('/') + "/auth/change-password";

        var req = new ChangePassReq { username = _currentTeacherId, old_password = oldPass, new_password = newPass };
        string json = JsonUtility.ToJson(req);

        var request = new UnityWebRequest(url, "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        ApplyTenantHeaders(request);

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            ShowToast("✅ Đổi mật khẩu thành công!", colGreen);
            var modal = _uiRoot.Q<VisualElement>("ModalChangePassword");
            if (modal != null) modal.style.display = DisplayStyle.None;
        }
        else
        {
            ShowToast($"❌ Lỗi: {request.downloadHandler.text}", colRed);
        }
    }

    IEnumerator PostAuthRegister(string username, string password)
    {
        _btnLoginInfo.SetEnabled(false);
        string url = cloudRunApiUrl.TrimEnd('/') + "/auth/register";

        string email = _inpLoginEmail?.value?.Trim() ?? ""; // [NEW] Read Email
        var reqData = new AuthReq { username = username, password = password, email = email };
        string json = JsonUtility.ToJson(reqData);

        var request = new UnityWebRequest(url, "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        _btnLoginInfo.SetEnabled(true);

        if (request.result != UnityWebRequest.Result.Success)
        {
            // Parse error message if possible
            ShowToast($"❌ Đăng ký thất bại: {request.error}", colRed);
            Debug.LogError(request.downloadHandler.text);
        }
        else
        {
            ShowToast("✅ Đăng ký thành công! Hãy đăng nhập.", colGreen);
            // Switch back to login mode
            ToggleRegisterMode();
            if (_inpLoginUser != null) _inpLoginUser.value = username;
            if (_inpLoginPass != null) _inpLoginPass.value = "";
        }
    }

    // ============================================================
    // TERM / WEEK / PHASE SETUP
    // ============================================================
    void SetupChartFilter()
    {
        // Week selector (reused name)
        _drpChartWeekSelector = _uiRoot.Q<DropdownField>("DrpChartWeekSelector");

        // Year/Semester dropdowns
        _drpSchoolYear = _uiRoot.Q<DropdownField>("DrpSchoolYear");
        _drpSemester = _uiRoot.Q<DropdownField>("DrpSemester");

        // Phase date inputs
        _inpPhase1Start = _uiRoot.Q<TextField>("InpPhase1Start");
        _inpPhase1End = _uiRoot.Q<TextField>("InpPhase1End");
        _inpPhase2Start = _uiRoot.Q<TextField>("InpPhase2Start");
        _inpPhase2End = _uiRoot.Q<TextField>("InpPhase2End");

        // ---- Fill dropdown choices ----
        if (_drpSchoolYear != null)
        {
            _drpSchoolYear.choices = BuildSchoolYearChoices();
            if (_drpSchoolYear.choices.Contains(_schoolYear))
                _drpSchoolYear.value = _schoolYear;
            else
                _drpSchoolYear.value = _drpSchoolYear.choices.Count > 0 ? _drpSchoolYear.choices[0] : _schoolYear;

            _schoolYear = _drpSchoolYear.value;
        }

        if (_drpSemester != null)
        {
            _drpSemester.choices = new List<string> { "Học kỳ 1", "Học kỳ 2" };
            _drpSemester.value = (_semester == 2) ? "Học kỳ 2" : "Học kỳ 1";
        }

        // Apply cached phase strings loaded from schedule
        if (_inpPhase1Start != null) _inpPhase1Start.value = _playerPhase1Start;
        if (_inpPhase1End != null) _inpPhase1End.value = _playerPhase1End;
        if (_inpPhase2Start != null) _inpPhase2Start.value = _playerPhase2Start;
        if (_inpPhase2End != null) _inpPhase2End.value = _playerPhase2End;

        // Build week dropdown from phases
        RebuildWeekChoicesFromPhases();

        // ---- Bind change events ----
        if (_drpSchoolYear != null)
        {
            _drpSchoolYear.RegisterValueChangedCallback(evt =>
            {
                _schoolYear = evt.newValue;
                SaveScheduleToLocal();
                UpdateStatsFromLogs();
            });
        }

        if (_drpSemester != null)
        {
            _drpSemester.RegisterValueChangedCallback(evt =>
            {
                _semester = evt.newValue.Contains("2") ? 2 : 1;
                SaveScheduleToLocal();
                UpdateStatsFromLogs();
            });
        }

        if (_drpChartWeekSelector != null)
        {
            _drpChartWeekSelector.RegisterValueChangedCallback(evt =>
            {
                if (_syncingWeekSelectors) return;
                _syncingWeekSelectors = true;

                // ✅ Sync to Stats week selector
                var drpStatsWeek = _uiRoot.Q<DropdownField>("DrpStatsWeekSelector");
                if (drpStatsWeek != null && drpStatsWeek.value != evt.newValue)
                    drpStatsWeek.SetValueWithoutNotify(evt.newValue);

                _syncingWeekSelectors = false;

                UpdateTrendChart();
                UpdateStatsTab();
                UpdateStatsFromLogs();
            });
        }

        // Phase date changed -> rebuild weeks
        if (_inpPhase1Start != null) _inpPhase1Start.RegisterValueChangedCallback(_ => OnPhaseDateEdited());
        if (_inpPhase1End != null) _inpPhase1End.RegisterValueChangedCallback(_ => OnPhaseDateEdited());
        if (_inpPhase2Start != null) _inpPhase2Start.RegisterValueChangedCallback(_ => OnPhaseDateEdited());
        if (_inpPhase2End != null) _inpPhase2End.RegisterValueChangedCallback(_ => OnPhaseDateEdited());

        // ✅ Hook "Thiết lập học kỳ" button if exists
        var btnEditTerm = _uiRoot.Q<Button>("BtnEditTerm");
        if (btnEditTerm != null)
        {
            btnEditTerm.clicked += ShowTermSetupModal;
        }
    }

    // ✅ Term Setup Modal Popup
    void ShowTermSetupModal()
    {
        // remove old if exists
        var old = _uiRoot.Q<VisualElement>("TermSetupOverlay");
        if (old != null) old.RemoveFromHierarchy();

        var overlay = new VisualElement { name = "TermSetupOverlay" };
        overlay.style.position = Position.Absolute;
        overlay.style.left = 0; overlay.style.right = 0;
        overlay.style.top = 0; overlay.style.bottom = 0;
        overlay.style.backgroundColor = new Color(0, 0, 0, 0.35f);
        overlay.style.justifyContent = Justify.Center;
        overlay.style.alignItems = Align.Center;

        var card = new VisualElement();
        card.style.width = 640;
        card.style.maxWidth = 720;
        card.style.backgroundColor = Color.white;
        card.style.borderTopLeftRadius = 16;
        card.style.borderTopRightRadius = 16;
        card.style.borderBottomLeftRadius = 16;
        card.style.borderBottomRightRadius = 16;
        card.style.paddingLeft = 18;
        card.style.paddingRight = 18;
        card.style.paddingTop = 16;
        card.style.paddingBottom = 14;

        var title = new Label("Thiết lập học kỳ");
        title.style.fontSize = 16;
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        title.style.marginBottom = 10;
        card.Add(title);

        // Row: Year + Semester
        var rowYS = new VisualElement();
        rowYS.style.flexDirection = FlexDirection.Row;
        rowYS.style.marginBottom = 10;

        VisualElement Field(string label, VisualElement input)
        {
            var wrap = new VisualElement();
            wrap.style.flexGrow = 1;
            wrap.style.marginRight = 12;

            var lb = new Label(label);
            lb.style.fontSize = 11;
            lb.style.color = new Color(0.45f, 0.45f, 0.45f);
            lb.style.marginBottom = 4;

            wrap.Add(lb);
            wrap.Add(input);
            return wrap;
        }

        var drpYear = new DropdownField();
        drpYear.choices = BuildSchoolYearChoices();
        drpYear.value = _drpSchoolYear != null ? _drpSchoolYear.value : (_schoolYear ?? drpYear.choices[0]);

        var drpSem = new DropdownField();
        drpSem.choices = new List<string> { "Học kỳ 1", "Học kỳ 2" };
        drpSem.value = (_semester == 2) ? "Học kỳ 2" : "Học kỳ 1";

        rowYS.Add(Field("Năm học", drpYear));
        rowYS.Add(Field("Học kỳ", drpSem));
        card.Add(rowYS);

        // Phase fields
        TextField MakeDateField(string val)
        {
            var tf = new TextField();
            tf.value = val ?? "";
            tf.style.height = 30;
            return tf;
        }

        var p1Row = new VisualElement();
        p1Row.style.flexDirection = FlexDirection.Row;
        p1Row.style.marginBottom = 8;

        var tfP1S = MakeDateField(_inpPhase1Start != null ? _inpPhase1Start.value : _playerPhase1Start);
        var tfP1E = MakeDateField(_inpPhase1End != null ? _inpPhase1End.value : _playerPhase1End);
        p1Row.Add(Field("Đợt 1 - Từ (dd/MM/yyyy)", tfP1S));
        p1Row.Add(Field("Đợt 1 - Đến (dd/MM/yyyy)", tfP1E));
        card.Add(p1Row);

        var p2Row = new VisualElement();
        p2Row.style.flexDirection = FlexDirection.Row;
        p2Row.style.marginBottom = 12;

        var tfP2S = MakeDateField(_inpPhase2Start != null ? _inpPhase2Start.value : _playerPhase2Start);
        var tfP2E = MakeDateField(_inpPhase2End != null ? _inpPhase2End.value : _playerPhase2End);
        p2Row.Add(Field("Đợt 2 - Từ (dd/MM/yyyy)", tfP2S));
        p2Row.Add(Field("Đợt 2 - Đến (dd/MM/yyyy)", tfP2E));
        card.Add(p2Row);

        // Buttons
        var btnRow = new VisualElement();
        btnRow.style.flexDirection = FlexDirection.Row;
        btnRow.style.justifyContent = Justify.FlexEnd;

        var btnCancel = new Button(() => overlay.RemoveFromHierarchy()) { text = "Hủy" };
        btnCancel.style.height = 34;
        btnCancel.style.marginRight = 10;
        btnCancel.style.backgroundColor = new Color(0, 0, 0, 0);
        btnCancel.style.borderLeftWidth = 0; btnCancel.style.borderRightWidth = 0;
        btnCancel.style.borderTopWidth = 0; btnCancel.style.borderBottomWidth = 0;
        btnCancel.style.color = new Color(0.45f, 0.45f, 0.45f);

        var btnSave = new Button(() =>
        {
            // Apply to hidden real fields (PhaseBar is hidden but exists)
            if (_drpSchoolYear != null) _drpSchoolYear.SetValueWithoutNotify(drpYear.value);
            if (_drpSemester != null) _drpSemester.SetValueWithoutNotify(drpSem.value);

            _schoolYear = drpYear.value;
            _semester = drpSem.value.Contains("2") ? 2 : 1;

            if (_inpPhase1Start != null) _inpPhase1Start.SetValueWithoutNotify(tfP1S.value);
            if (_inpPhase1End != null) _inpPhase1End.SetValueWithoutNotify(tfP1E.value);
            if (_inpPhase2Start != null) _inpPhase2Start.SetValueWithoutNotify(tfP2S.value);
            if (_inpPhase2End != null) _inpPhase2End.SetValueWithoutNotify(tfP2E.value);

            // Update cached strings
            _playerPhase1Start = tfP1S.value ?? "";
            _playerPhase1End = tfP1E.value ?? "";
            _playerPhase2Start = tfP2S.value ?? "";
            _playerPhase2End = tfP2E.value ?? "";

            // Rebuild weeks + save + refresh
            OnPhaseDateEdited();
            SaveScheduleToLocal();
            StartCoroutine(PushScheduleBundleToServer(silent: true)); // Sync lên server

            UpdateStatsTab();
            UpdateTrendChart();
            UpdateHeaderDateWeek(force: true); // [NEW] Cập nhật header ngay lập tức

            ShowToast("✅ Đã lưu thiết lập học kỳ", colGreen);
            overlay.RemoveFromHierarchy();
        })
        { text = "Lưu" };

        btnSave.style.height = 34;
        btnSave.style.backgroundColor = colAccent;
        btnSave.style.color = Color.white;
        btnSave.style.borderLeftWidth = 0; btnSave.style.borderRightWidth = 0;
        btnSave.style.borderTopWidth = 0; btnSave.style.borderBottomWidth = 0;
        btnSave.style.borderTopLeftRadius = 10;
        btnSave.style.borderTopRightRadius = 10;
        btnSave.style.borderBottomLeftRadius = 10;
        btnSave.style.borderBottomRightRadius = 10;

        btnRow.Add(btnCancel);
        btnRow.Add(btnSave);
        card.Add(btnRow);

        // Close when click outside card
        overlay.RegisterCallback<PointerDownEvent>(evt =>
        {
            if (evt.target == overlay) overlay.RemoveFromHierarchy();
        });

        overlay.Add(card);
        _uiRoot.Add(overlay);
    }

    private void OnPhaseDateEdited()
    {
        _playerPhase1Start = _inpPhase1Start != null ? _inpPhase1Start.value : _playerPhase1Start;
        _playerPhase1End = _inpPhase1End != null ? _inpPhase1End.value : _playerPhase1End;
        _playerPhase2Start = _inpPhase2Start != null ? _inpPhase2Start.value : _playerPhase2Start;
        _playerPhase2End = _inpPhase2End != null ? _inpPhase2End.value : _playerPhase2End;

        RebuildWeekChoicesFromPhases();
        SaveScheduleToLocal();
        UpdateStatsFromLogs();
    }

    private List<string> BuildSchoolYearChoices()
    {
        var list = new List<string>();
        DateTime now = DateTime.Now;
        int baseStart = (now.Month >= 8) ? now.Year : now.Year - 1;
        for (int y = baseStart - 2; y <= baseStart + 1; y++)
            list.Add($"{y}-{y + 1}");
        return list;
    }

    private void RebuildWeekChoicesFromPhases()
    {
        if (_drpChartWeekSelector == null) return;

        var options = new List<string> { "Tuần này", "Tất cả" };
        _weekMap.Clear();

        var allWeeks = new Dictionary<DateTime, (DateTime start, DateTime end)>();
        AddWeeksFromPhase(_inpPhase1Start?.value, _inpPhase1End?.value, allWeeks);
        AddWeeksFromPhase(_inpPhase2Start?.value, _inpPhase2End?.value, allWeeks);

        foreach (var kv in allWeeks.OrderBy(k => k.Key))
        {
            var w = kv.Value;
            string label = $"{w.start:dd/MM/yyyy}-{w.end:dd/MM/yyyy}";
            options.Add(label);
            _weekMap[label] = w;
        }

        _drpChartWeekSelector.choices = options;
        if (string.IsNullOrEmpty(_drpChartWeekSelector.value) || !options.Contains(_drpChartWeekSelector.value))
            _drpChartWeekSelector.value = "Tuần này";
    }

    private void AddWeeksFromPhase(string startStr, string endStr, Dictionary<DateTime, (DateTime start, DateTime end)> map)
    {
        if (!TryParseVNDate(startStr, out DateTime start)) return;
        if (!TryParseVNDate(endStr, out DateTime end)) return;
        if (end < start) return;

        foreach (var w in BuildWeekRanges(start, end))
        {
            if (!map.ContainsKey(w.start))
                map[w.start] = w;
        }
    }

    private IEnumerable<(DateTime start, DateTime end)> BuildWeekRanges(DateTime from, DateTime to)
    {
        DateTime cur = GetStartOfWeek(from).Date;
        DateTime last = GetStartOfWeek(to).Date;

        while (cur <= last)
        {
            DateTime weekStart = cur;
            DateTime weekEnd = cur.AddDays(6).Date.AddHours(23).AddMinutes(59).AddSeconds(59);

            if (weekEnd >= from.Date && weekStart <= to.Date)
                yield return (weekStart, weekEnd);

            cur = cur.AddDays(7);
        }
    }

    private bool TryParseVNDate(string s, out DateTime dt)
    {
        dt = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        return DateTime.TryParseExact(
            s.Trim(), "dd/MM/yyyy",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out dt
        );
    }

    // ==========================================
    // HEADER DATE/WEEK DISPLAY (Phase-based)
    // ==========================================
    private void UpdateHeaderDateWeek(bool force = false)
    {
        if (_uiLblDateWeek == null) return;

        // Giảm tính toán mỗi frame
        int sec = DateTime.Now.Second;
        if (!force && sec == _lastHeaderSecond) return;
        _lastHeaderSecond = sec;

        var now = DateTime.Now;

        string dayName = now.ToString("dddd", new System.Globalization.CultureInfo("vi-VN"));
        // TitleCase cho đẹp
        dayName = System.Globalization.CultureInfo.GetCultureInfo("vi-VN").TextInfo.ToTitleCase(dayName);

        string datePart = $"{dayName}, {now:dd/MM/yyyy}";

        if (TryGetCurrentPhaseWeek(now, out int phase, out int weekNo, out DateTime ws, out DateTime we))
        {
            string text = $"{datePart} - Đợt {phase} - Tuần {weekNo} ({ws:dd/MM}-{we:dd/MM})";
            if (force || text != _cachedDateWeekText)
            {
                _cachedDateWeekText = text;
                _uiLblDateWeek.text = text;
            }
            return;
        }

        // Fallback: tuần trong năm (nếu chưa nằm trong đợt 1/2 hoặc chưa setup)
        var cultureInfo = System.Globalization.CultureInfo.CurrentCulture;
        int weekOfYear = cultureInfo.Calendar.GetWeekOfYear(now, System.Globalization.CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
        string fallbackText = $"{datePart} - Tuần {weekOfYear}";

        if (force || fallbackText != _cachedDateWeekText)
        {
            _cachedDateWeekText = fallbackText;
            _uiLblDateWeek.text = fallbackText;
        }
    }

    private bool TryGetCurrentPhaseWeek(DateTime now, out int phase, out int weekNo, out DateTime weekStart, out DateTime weekEnd)
    {
        phase = 0; weekNo = 0; weekStart = default; weekEnd = default;

        // Đợt 1
        if (TryParseVNDate(_playerPhase1Start, out DateTime p1s) && TryParseVNDate(_playerPhase1End, out DateTime p1e) && p1e >= p1s)
        {
            var weeks = BuildWeekRanges(p1s, p1e).ToList();
            for (int i = 0; i < weeks.Count; i++)
            {
                var w = weeks[i];
                if (now >= w.start && now <= w.end)
                {
                    phase = 1;
                    weekNo = i + 1;
                    weekStart = w.start.Date;
                    weekEnd = w.start.Date.AddDays(6);
                    return true;
                }
            }
        }

        // Đợt 2
        if (TryParseVNDate(_playerPhase2Start, out DateTime p2s) && TryParseVNDate(_playerPhase2End, out DateTime p2e) && p2e >= p2s)
        {
            var weeks = BuildWeekRanges(p2s, p2e).ToList();
            for (int i = 0; i < weeks.Count; i++)
            {
                var w = weeks[i];
                if (now >= w.start && now <= w.end)
                {
                    phase = 2;
                    weekNo = i + 1;
                    weekStart = w.start.Date;
                    weekEnd = w.start.Date.AddDays(6);
                    return true;
                }
            }
        }

        return false;
    }

    private DateTime GetStartOfWeek(DateTime dt)
    {
        int diff = (7 + (dt.DayOfWeek - DayOfWeek.Monday)) % 7;
        return dt.AddDays(-1 * diff).Date;
    }

    private int GetIso8601WeekOfYear(DateTime time)
    {
        DayOfWeek day = System.Globalization.CultureInfo.InvariantCulture.Calendar.GetDayOfWeek(time);
        if (day >= DayOfWeek.Monday && day <= DayOfWeek.Wednesday)
        {
            time = time.AddDays(3);
        }
        return System.Globalization.CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(time, System.Globalization.CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
    }

    // [NEW] Save schedule + term config + class selection to local file
    void SaveScheduleToLocal()
    {
        try
        {
            var wrapper = new ScheduleDataWrapper
            {
                sessions = _scheduleList,
                schoolYear = _schoolYear,
                semester = _semester,
                phase1Start = _inpPhase1Start != null ? _inpPhase1Start.value : _playerPhase1Start,
                phase1End = _inpPhase1End != null ? _inpPhase1End.value : _playerPhase1End,
                phase2Start = _inpPhase2Start != null ? _inpPhase2Start.value : _playerPhase2Start,
                phase2End = _inpPhase2End != null ? _inpPhase2End.value : _playerPhase2End,
                // Class switcher state
                selectedClassCode = _currentClassCode,
                selectedTermId = CurrentTermId
            };

            string json = JsonUtility.ToJson(wrapper, true);
            File.WriteAllText(ScheduleDataPath, json);
            Debug.Log($"[Schedule] Saved {_scheduleList.Count} sessions + class={_currentClassCode} to local");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Schedule] Failed to save: {e.Message}");
        }
    }

    // [NEW] Load schedule + term config + class selection from local file
    void LoadScheduleFromLocal()
    {
        try
        {
            if (!File.Exists(ScheduleDataPath))
            {
                Debug.Log("[Schedule] No local schedule file found");
                _scheduleList.Clear(); // [FIX] Clear global list if no file exists to avoid state leak
                return;
            }

            string json = File.ReadAllText(ScheduleDataPath);
            var wrapper = JsonUtility.FromJson<ScheduleDataWrapper>(json);
            if (wrapper != null)
            {
                _scheduleList = wrapper.sessions ?? new List<ScheduleSession>();
                _schoolYear = string.IsNullOrEmpty(wrapper.schoolYear) ? _schoolYear : wrapper.schoolYear;
                _semester = (wrapper.semester == 0) ? _semester : wrapper.semester;
                _playerPhase1Start = wrapper.phase1Start ?? "";
                _playerPhase1End = wrapper.phase1End ?? "";
                _playerPhase2Start = wrapper.phase2Start ?? "";
                _playerPhase2End = wrapper.phase2End ?? "";

                // Restore class selection
                if (!string.IsNullOrEmpty(wrapper.selectedClassCode))
                    _currentClassCode = wrapper.selectedClassCode;
                if (!string.IsNullOrEmpty(wrapper.selectedTermId))
                    _currentTermId = wrapper.selectedTermId;

                Debug.Log($"[Schedule] Loaded {_scheduleList.Count} sessions, class={_currentClassCode}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[Schedule] Failed to load: {e.Message}");
        }
    }

    // ====== SYNC HELPERS ======
    private void ApplyTeacherHeaderOnly(UnityWebRequest req)
    {
        if (req == null) return;
        if (!string.IsNullOrEmpty(_currentTeacherId))
            req.SetRequestHeader("X-Teacher-ID", _currentTeacherId);
    }

    private IEnumerator PushScheduleBundleToServer(bool silent = true)
    {
        if (!_isLoggedIn || string.IsNullOrEmpty(_currentTeacherId)) yield break;

        string url = cloudRunApiUrl.TrimEnd('/') + "/schedule_bundle";
        var wrapper = new ScheduleDataWrapper
        {
            sessions = _scheduleList ?? new List<ScheduleSession>(),
            schoolYear = _schoolYear,
            semester = _semester,
            phase1Start = _playerPhase1Start,
            phase1End = _playerPhase1End,
            phase2Start = _playerPhase2Start,
            phase2End = _playerPhase2End,
            selectedClassCode = _currentClassCode,
            selectedTermId = CurrentTermId
        };

        string json = JsonUtility.ToJson(wrapper, true);

        using (var req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            ApplyTeacherHeaderOnly(req);

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                if (!silent) ShowToast($"⚠️ Sync lịch dạy thất bại: {req.error}", colOrange);
                Debug.LogError("[Sync] PushScheduleBundle failed: " + req.downloadHandler.text);
            }
            else
            {
                Debug.Log("[Sync] PushScheduleBundle OK");
            }
        }
    }

    private IEnumerator PullScheduleBundleFromServer(bool autoSwitchTenant = true, bool silent = true)
    {
        if (!_isLoggedIn || string.IsNullOrEmpty(_currentTeacherId)) yield break;

        string url = cloudRunApiUrl.TrimEnd('/') + "/schedule_bundle?t=" + DateTime.Now.Ticks;

        using (var req = UnityWebRequest.Get(url))
        {
            req.downloadHandler = new DownloadHandlerBuffer();
            ApplyTeacherHeaderOnly(req);

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                if (!silent) ShowToast($"⚠️ Tải lịch dạy thất bại: {req.error}", colOrange);
                Debug.LogError("[Sync] PullScheduleBundle failed: " + req.downloadHandler.text);
                yield break;
            }

            ScheduleBundleResp res = null;
            try { res = JsonUtility.FromJson<ScheduleBundleResp>(req.downloadHandler.text); }
            catch (Exception e)
            {
                Debug.LogError("[Sync] PullScheduleBundle parse error: " + e.Message);
                yield break;
            }

            if (res == null || res.status != "success") yield break;

            // Apply
            if (res.sessions != null)
                _scheduleList = res.sessions;

            if (!string.IsNullOrEmpty(res.schoolYear)) _schoolYear = res.schoolYear;
            if (res.semester != 0) _semester = res.semester;

            _playerPhase1Start = res.phase1Start ?? _playerPhase1Start;
            _playerPhase1End   = res.phase1End   ?? _playerPhase1End;
            _playerPhase2Start = res.phase2Start ?? _playerPhase2Start;
            _playerPhase2End   = res.phase2End   ?? _playerPhase2End;

            // Restore selected class if exists
            if (!string.IsNullOrEmpty(res.selectedClassCode))
                _currentClassCode = res.selectedClassCode;

            _currentTermId = CurrentTermId;

            SaveScheduleToLocal();
            RenderScheduleTab();
            RenderTodaySchedule();

            EnsureDefaultClassSelected();
            UpdateClassLabels();

            Debug.Log($"[Sync] Pulled schedule bundle: sessions={_scheduleList?.Count ?? 0}, class={_currentClassCode}, term={_currentTermId}");

            if (autoSwitchTenant && !string.IsNullOrEmpty(_currentClassCode))
            {
                // Force SwitchClass to reload caches + fetch logs/profiles đúng lớp
                var target = _currentClassCode;
                _currentClassCode = ""; // force
                SwitchClass(target);
            }

            // Luôn pull roster khi đăng nhập, không phụ thuộc schedule bundle
            // Đảm bảo máy khác cũng thấy danh sách lớp đã import
            StartCoroutine(PullRosterFromServer(silent: true));
        }
    }

    private IEnumerator PushRosterToServer(bool silent = true)
    {
        if (!_isLoggedIn || string.IsNullOrEmpty(_currentTeacherId)) yield break;
        if (_studentDataList == null || _studentDataList.Count == 0) yield break;

        string url = cloudRunApiUrl.TrimEnd('/') + "/roster/import";

        var reqObj = new RosterImportReq
        {
            students = _studentDataList.Select(s => new RosterStudentDto
            {
                name = s.Name ?? "",
                mssv = s.MSSV ?? "",
                registered = s.IsRegistered
            }).ToArray()
        };

        string json = JsonUtility.ToJson(reqObj, true);

        using (var req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");

            // roster is tenant-based
            ApplyTenantHeaders(req);

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                if (!silent) ShowToast($"⚠️ Sync DS lớp thất bại: {req.error}", colOrange);
                Debug.LogError("[Sync] PushRoster failed: " + req.downloadHandler.text);
            }
            else
            {
                Debug.Log("[Sync] PushRoster OK");
            }
        }
    }

    private IEnumerator PullRosterFromServer(bool silent = true)
    {
        if (!_isLoggedIn || string.IsNullOrEmpty(_currentTeacherId)) yield break;
        if (string.IsNullOrEmpty(_currentClassCode)) yield break;

        string url = cloudRunApiUrl.TrimEnd('/') + "/roster?t=" + DateTime.Now.Ticks;

        using (var req = UnityWebRequest.Get(url))
        {
            req.downloadHandler = new DownloadHandlerBuffer();
            ApplyTenantHeaders(req);

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                if (!silent) ShowToast($"⚠️ Tải DS lớp thất bại: {req.error}", colOrange);
                Debug.LogError("[Sync] PullRoster failed: " + req.downloadHandler.text);
                yield break;
            }

            RosterGetResp res = null;
            try { res = JsonUtility.FromJson<RosterGetResp>(req.downloadHandler.text); }
            catch (Exception e)
            {
                Debug.LogError("[Sync] PullRoster parse error: " + e.Message);
                yield break;
            }

            if (res == null || res.status != "success" || res.students == null) yield break;

            _studentDataList = new List<StudentStat>();
            foreach (var st in res.students)
            {
                _studentDataList.Add(new StudentStat
                {
                    Name = st.name ?? "",
                    MSSV = st.mssv ?? "",
                    IsRegistered = st.registered
                });
            }

            SaveStudentDataListToFile();
            FetchAndRenderStudentList();
            UpdateStatsFromLogs();

            Debug.Log($"[Sync] Pulled roster: {_studentDataList.Count} students");
        }
    }


    // [NEW] Setup Day Selector for Home Stats
    void SetupDaySelector()
    {
        _drpDaySelector = _uiRoot.Q<DropdownField>("DrpDaySelector");
        if (_drpDaySelector != null)
        {
            var options = new List<string>();
            options.Add("Hôm nay");

            // Add last 7 days
            for (int i = 1; i <= 7; i++)
            {
                DateTime pastDay = DateTime.Now.AddDays(-i);
                string dayName = pastDay.ToString("dddd", new System.Globalization.CultureInfo("vi-VN"));
                string label = $"{dayName}, {pastDay:dd/MM}";
                options.Add(label);
            }

            _drpDaySelector.choices = options;
            _drpDaySelector.value = "Hôm nay";

            _drpDaySelector.RegisterValueChangedCallback(evt =>
            {
                UpdateStatsFromLogs();
                UpdateTrendChart(); // Refresh chart when day changes
            });
        }
    }

    // ============================================================
    // DASHBOARD STATS INITIALIZATION
    // ============================================================

    void InitDashboardStats()
    {
        SetupChartFilter(); // [NEW] Setup Week Filter
        SetupDaySelector(); // [NEW] Setup Day Selector for Home Stats

        // [FIX] Load roster first so _studentDataList has data
        LoadStudentDataListFromFile();

        // [FIX] Set total from roster, not profiles
        var lblStatTotal = _uiRoot.Q<Label>("LblStatTotal");
        if (lblStatTotal != null)
            lblStatTotal.text = (_studentDataList?.Count ?? 0).ToString();

        // Fetch profiles for registration status (but don't use for total)
        StartCoroutine(GetProfiles((profiles) =>
        {
            if (profiles != null)
            {
                _allProfilesCache = profiles.ToList(); // Cache for calculations

                Debug.Log($"[InitStats] Roster: {_studentDataList?.Count ?? 0}, Registered: {profiles.Length}");

                // Recalculate stats now that we have profiles
                UpdateStatsFromLogs();
            }
        }));

        // Update other stats from logs (will be updated again when logs are fetched)
        UpdateStatsFromLogs();
    }

    void UpdateStatsFromLogs()
    {
        if (_allLogs == null) return;

        // Determine Date Range
        string selection = _drpChartWeekSelector?.value ?? "Tuần này";
        DateTime start, end;

        if (selection == "Tuần này")
        {
            start = GetStartOfWeek(DateTime.Now);
            end = start.AddDays(6).AddHours(23).AddMinutes(59);
        }
        else if (selection == "Tất cả")
        {
            start = DateTime.MinValue;
            end = DateTime.MaxValue;
        }
        else
        {
            // Parse "Tuần X (dd/MM - dd/MM)" - Simplest is to rely on index or re-calculate
            // Or just parsing regex. Let's try to infer from string if it matches format
            try
            {
                int open = selection.IndexOf('(');
                int close = selection.IndexOf(')');
                if (open > 0 && close > open)
                {
                    string dates = selection.Substring(open + 1, close - open - 1); // "15/12 - 21/12"
                    string[] parts = dates.Split('-');
                    string startS = parts[0].Trim();
                    // Assume current year or previous year? complicated.
                    // Simplified: just match based on date string in log if possible, OR
                    // Re-calculate the offset based on index in dropdown is safer?
                    // But "choices" list might change.

                    // Fallback: Use "Tuần này" logic
                    start = GetStartOfWeek(DateTime.Now);
                    end = start.AddDays(6);
                }
                else
                {
                    start = GetStartOfWeek(DateTime.Now);
                    end = start.AddDays(6);
                }
            }
            catch
            {
                start = GetStartOfWeek(DateTime.Now);
                end = start.AddDays(6);
            }
        }

        // Filter Logs - HOME PAGE shows selected day
        // Date format in logs is "dd/MM/yyyy"
        DateTime selectedDate = DateTime.Now;
        string daySel = _drpDaySelector?.value ?? "Hôm nay";

        if (daySel != "Hôm nay")
        {
            // Format: "Thứ X, dd/MM" - extract dd/MM
            int comma = daySel.IndexOf(',');
            if (comma > 0 && daySel.Length > comma + 2)
            {
                string dateStr = daySel.Substring(comma + 2).Trim(); // "dd/MM"
                if (DateTime.TryParseExact(dateStr + "/" + DateTime.Now.Year, "dd/MM/yyyy", null, System.Globalization.DateTimeStyles.None, out DateTime parsed))
                {
                    selectedDate = parsed;
                }
            }
        }

        string selectedDateStr = selectedDate.ToString("dd/MM/yyyy");
        var relevantLogs = _allLogs.Where(l => l.date == selectedDateStr).ToList();

        // Count present: exclude logs with absent status
        int presentCount = relevantLogs
            .Where(l => !IsAbsentStatusHelper(l.status))
            .Select(l => l.mssv)
            .Distinct()
            .Count();

        // Count Late (based on status)
        int lateCount = relevantLogs.Count(l => l.status?.ToLower() == "late" || l.status?.ToLower() == "trễ");

        if (_lblStatPresent != null) _lblStatPresent.text = presentCount.ToString();
        if (_lblStatLate != null) _lblStatLate.text = lateCount.ToString();

        // ✅ Calculate total and absent from ROSTER (not profiles)
        if (_studentDataList == null || _studentDataList.Count == 0)
            LoadStudentDataListFromFile();

        int total = (_studentDataList != null && _studentDataList.Count > 0) ? _studentDataList.Count : 0;
        int absent = 0;

        // Only calculate absent when class has FINISHED (avoid showing "absent" during class)
        if (HasClass(selectedDate) && HasClassFinished(selectedDate))
        {
            absent = Mathf.Max(0, total - presentCount);
        }

        var lblStatTotal = _uiRoot.Q<Label>("LblStatTotal");
        if (lblStatTotal != null) lblStatTotal.text = total.ToString();
        if (_lblStatAbsent != null) _lblStatAbsent.text = absent.ToString();
    }

    // [NEW] Helper to check class exist (filters by current class)
    bool HasClass(DateTime date)
    {
        // ✅ OPTIMAL: Nếu không có TKB thì KHÔNG suy ra "có lớp" bừa.
        // Chỉ coi là có lớp nếu ngày đó thực sự có log điểm danh.
        if (_scheduleList == null || _scheduleList.Count == 0)
        {
            string dStr = date.ToString("dd/MM/yyyy");
            return _allLogs != null && _allLogs.Any(l => l.date == dStr);
        }

        // C# DayOfWeek: Sunday=0..Saturday=6
        // Schedule dayOfWeek: Monday=2..Sunday=8
        int dow = (int)date.DayOfWeek;
        int scheduleDow = (dow == 0) ? 8 : (dow + 1);

        IEnumerable<ScheduleSession> sessions = _scheduleList;

        // ✅ Nếu schedule có classCode và bạn đang chọn lớp -> lọc theo lớp
        bool scheduleHasClassCode = _scheduleList.Any(s => !string.IsNullOrEmpty(s.classCode));
        if (scheduleHasClassCode && !string.IsNullOrEmpty(_currentClassCode))
        {
            string cc = _currentClassCode.Trim();
            sessions = sessions.Where(s => (s.classCode ?? "").Trim().Equals(cc, StringComparison.OrdinalIgnoreCase));
        }

        return sessions.Any(s => s.dayOfWeek == scheduleDow);
    }

    // [NEW] Helper to check if class session has finished (filters by current class)
    bool HasClassFinished(DateTime date)
    {
        if (date.Date < DateTime.Today) return true;
        if (date.Date > DateTime.Today) return false;

        if (_scheduleList == null || _scheduleList.Count == 0) return false;

        int dow = (int)date.DayOfWeek;
        int scheduleDow = (dow == 0) ? 8 : (dow + 1);

        IEnumerable<ScheduleSession> sessions = _scheduleList.Where(s => s.dayOfWeek == scheduleDow);

        bool scheduleHasClassCode = _scheduleList.Any(s => !string.IsNullOrEmpty(s.classCode));
        if (scheduleHasClassCode && !string.IsNullOrEmpty(_currentClassCode))
        {
            string cc = _currentClassCode.Trim();
            sessions = sessions.Where(s => (s.classCode ?? "").Trim().Equals(cc, StringComparison.OrdinalIgnoreCase));
        }

        var sessionsToday = sessions.ToList();
        if (sessionsToday.Count == 0) return false;

        DateTime latestEnd = DateTime.MinValue;
        foreach (var s in sessionsToday)
        {
            int endP = s.endPeriod > 0 ? s.endPeriod : s.startPeriod;
            var t = GetPeriodTime(endP);
            DateTime endTime = new DateTime(date.Year, date.Month, date.Day, t.endHour, t.endMin, 0);
            if (endTime > latestEnd) latestEnd = endTime;
        }

        return DateTime.Now >= latestEnd;
    }

    // [FIX #8] Helper: Get selected date from Home day selector
    DateTime GetSelectedHomeDate()
    {
        DateTime selectedDate = DateTime.Today;
        string daySel = _drpDaySelector?.value ?? "Hôm nay";

        if (daySel != "Hôm nay")
        {
            int comma = daySel.IndexOf(',');
            if (comma > 0)
            {
                string dateStr = daySel.Substring(comma + 1).Trim();
                if (DateTime.TryParseExact(
                        dateStr + "/" + DateTime.Now.Year,
                        "dd/MM/yyyy",
                        null,
                        System.Globalization.DateTimeStyles.None,
                        out DateTime parsed))
                {
                    selectedDate = parsed;
                }
            }
        }
        return selectedDate;
    }


    // Note: IsAbsentStatusHelper declared above (in multi-class helpers section)

    // [FIX #8] Helper: Count absent sessions in range (only finished sessions)
    int CountAbsentInRange(string mssv, DateTime start, DateTime end)
    {
        if (string.IsNullOrWhiteSpace(mssv)) return 0;
        mssv = mssv.Trim();

        int absent = 0;
        for (var d = start.Date; d <= end.Date; d = d.AddDays(1))
        {
            if (!HasClass(d)) continue;
            if (!HasClassFinished(d)) continue;

            string dStr = d.ToString("dd/MM/yyyy");

            bool hasPresentLog = _allLogs != null && _allLogs.Any(l =>
                l.date == dStr &&
                (l.mssv ?? "").Trim() == mssv &&
                !IsAbsentStatusHelper(l.status)
            );

            if (!hasPresentLog) absent++;
        }
        return absent;
    }

    // [NEW] Export CSV
    void ExportStatsToCSV()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("MSSV,Ho Ten,So Lan Tre,So Lan Vang,Chi Tiet");

            GetSelectedDateRange(out DateTime start, out DateTime end);

            foreach (var student in _studentDataList)
            {
                int late = 0;
                int absent = 0;

                // Check range
                for (var d = start.Date; d <= end.Date; d = d.AddDays(1))
                {
                    if (HasClass(d))
                    {
                        string dStr = d.ToString("dd/MM/yyyy");
                        var log = _allLogs.FirstOrDefault(l => l.mssv == student.MSSV && l.date == dStr);

                        if (log == null) absent++;
                        else if (log.status == "Late") late++;
                    }
                }

                sb.AppendLine($"{student.MSSV},{student.Name},{late},{absent},");
            }

            string path = Path.Combine(Application.persistentDataPath, $"AttendanceReport_{DateTime.Now:yyyyMMdd_HHmm}.csv");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8); // UTF8 BOM?
            ShowToast($"✅ Đã xuất file: {path}", colGreen);
            Application.OpenURL(path); // Open if possible
        }
        catch (Exception e)
        {
            ShowToast($"❌ Lỗi xuất file: {e.Message}", colRed);
        }
    }

    // ============================================================
    // STUDENT LIST & SEARCH LOGIC (Using ScrollView)
    // ============================================================

    private VisualElement _studentListContent;

    void SetupStudentListLogic()
    {
        _studentListContent = _uiRoot.Q<VisualElement>("StudentListContent");
        _inpSearchStudent = _uiRoot.Q<TextField>("InpSearchStudent");

        if (_inpSearchStudent != null)
        {
            _inpSearchStudent.RegisterValueChangedCallback(evt =>
            {
                FilterStudentList(evt.newValue);
            });

            // [FIX] Prevent Unity crash on Backspace when text is empty
            _inpSearchStudent.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Backspace && string.IsNullOrEmpty(_inpSearchStudent.value))
                {
                    evt.StopPropagation();
                    evt.PreventDefault();
                }
            }, TrickleDown.TrickleDown);
        }

        Debug.Log($"[StudentList] Setup OK. Content container: {_studentListContent != null}");

        // [NEW] Bind clear all button
        var btnClearAll = _uiRoot.Q<Button>("BtnClearAllStudents");
        if (btnClearAll != null)
        {
            btnClearAll.clicked += () => ClearAllStudentsWithConfirm();
        }

        // [NEW] Bind finalize session button ("Chốt buổi")
        var btnFinalize = _uiRoot.Q<Button>("BtnFinalizeSession");
        if (btnFinalize != null)
        {
            btnFinalize.clicked += () => StartCoroutine(FinalizeAndSyncToGoogleSheet());
        }

        // [NEW] Re-bind Google buttons explicitly
        SetupConnectGoogleButton();
        SetupOpenGSheetButton();
    }

    void ClearAllStudentsWithConfirm()
    {
        // Show confirmation modal
        var overlay = new VisualElement();
        overlay.style.position = Position.Absolute;
        overlay.style.left = overlay.style.top = overlay.style.right = overlay.style.bottom = 0;
        overlay.style.backgroundColor = new Color(0, 0, 0, 0.6f);
        overlay.style.justifyContent = Justify.Center;
        overlay.style.alignItems = Align.Center;

        var card = new VisualElement();
        card.style.backgroundColor = Color.white;
        card.style.borderTopLeftRadius = card.style.borderTopRightRadius =
            card.style.borderBottomLeftRadius = card.style.borderBottomRightRadius = 16;
        card.style.paddingTop = card.style.paddingBottom = 24;
        card.style.paddingLeft = card.style.paddingRight = 28;
        card.style.minWidth = 350;

        var title = new Label("⚠️ Xác nhận xóa");
        title.style.fontSize = 18;
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        title.style.color = ColorFromHex("#e74c3c");
        title.style.marginBottom = 12;
        card.Add(title);

        var msg = new Label("Bạn có chắc muốn xóa TẤT CẢ danh sách sinh viên đã import?\n\nThao tác này không thể hoàn tác!");
        msg.style.fontSize = 14;
        msg.style.color = ColorFromHex("#2d3436");
        msg.style.marginBottom = 20;
        msg.style.whiteSpace = WhiteSpace.Normal;
        card.Add(msg);

        var btnRow = new VisualElement();
        btnRow.style.flexDirection = FlexDirection.Row;
        btnRow.style.justifyContent = Justify.FlexEnd;

        var btnCancel = new Button(() => overlay.RemoveFromHierarchy());
        btnCancel.text = "Hủy";
        btnCancel.style.paddingLeft = btnCancel.style.paddingRight = 20;
        btnCancel.style.paddingTop = btnCancel.style.paddingBottom = 10;
        btnCancel.style.marginRight = 10;
        btnCancel.style.backgroundColor = ColorFromHex("#e9ecef");
        btnCancel.style.color = ColorFromHex("#2d3436");
        btnCancel.style.borderTopLeftRadius = btnCancel.style.borderTopRightRadius =
            btnCancel.style.borderBottomLeftRadius = btnCancel.style.borderBottomRightRadius = 8;
        btnRow.Add(btnCancel);

        var btnConfirm = new Button(() =>
        {
            ClearAllStudentsData();
            overlay.RemoveFromHierarchy();
        });
        btnConfirm.text = "🗑️ Xóa tất cả";
        btnConfirm.style.paddingLeft = btnConfirm.style.paddingRight = 20;
        btnConfirm.style.paddingTop = btnConfirm.style.paddingBottom = 10;
        btnConfirm.style.backgroundColor = ColorFromHex("#e74c3c");
        btnConfirm.style.color = Color.white;
        btnConfirm.style.borderTopLeftRadius = btnConfirm.style.borderTopRightRadius =
            btnConfirm.style.borderBottomLeftRadius = btnConfirm.style.borderBottomRightRadius = 8;
        btnRow.Add(btnConfirm);

        card.Add(btnRow);
        overlay.Add(card);
        _uiRoot.Add(overlay);
    }

    void ClearAllStudentsData()
    {
        // 1. Clear in-memory list
        _studentDataList.Clear();
        Debug.Log("[ClearAll] Cleared _studentDataList");

        // 2. Clear UI
        if (_studentListContent != null)
            _studentListContent.Clear();

        if (_studentListView != null)
        {
            _studentListView.itemsSource = _studentDataList;
            _studentListView.Rebuild();
        }

        // 3. Delete saved class list file (if exists)
        string classListPath = Path.Combine(Application.persistentDataPath, $"classList_{_currentTeacherId}_{CurrentTermId}_{_currentClassCode}.json");
        if (File.Exists(classListPath))
        {
            File.Delete(classListPath);
            Debug.Log($"[ClearAll] Deleted saved file: {classListPath}");
        }

        // 4. Also try generic path
        string genericPath = Path.Combine(Application.persistentDataPath, "importedStudents.json");
        if (File.Exists(genericPath))
        {
            File.Delete(genericPath);
            Debug.Log($"[ClearAll] Deleted: {genericPath}");
        }

        ShowToast("✅ Đã xóa tất cả danh sách sinh viên!", colGreen);
    }

    void FetchAndRenderStudentList()
    {
        StartCoroutine(GetProfiles((profiles) =>
        {
            if (profiles != null)
            {
                _allProfilesCache = profiles.ToList();
                Debug.Log($"[StudentList] Fetched {_allProfilesCache.Count} profiles");
                FilterStudentList(_inpSearchStudent?.value ?? "");
            }
        }));
    }

    void FilterStudentList(string keyword)
    {
        if (_studentListContent == null)
        {
            Debug.LogWarning("[StudentList] _studentListContent is null!");
            return;
        }

        // Clear all existing items
        _studentListContent.Clear();

        string normKeyword = RemoveDiacritics(keyword ?? "").ToLower().Trim();

        // [NEW] Use imported class list if available, otherwise use profiles
        if (_studentDataList != null && _studentDataList.Count > 0)
        {
            Debug.Log($"[Filter] Using imported class list: {_studentDataList.Count} students");

            List<StudentStat> results;
            if (string.IsNullOrEmpty(normKeyword))
            {
                results = _studentDataList;
            }
            else
            {
                results = _studentDataList.Where(s =>
                {
                    string normName = RemoveDiacritics(s.Name ?? "").ToLower();
                    string normMssv = RemoveDiacritics(s.MSSV ?? "").ToLower();
                    return normName.Contains(normKeyword) || normMssv.Contains(normKeyword);
                }).ToList();
            }

            Debug.Log($"[Filter] Found {results.Count} matching students");

            for (int i = 0; i < results.Count; i++)
            {
                var row = CreateStudentRowFromStat(results[i], i + 1);
                _studentListContent.Add(row);
            }
        }
        else
        {
            // Fallback: Use profiles from API
            Debug.Log($"[Filter] Using profiles from API: {_allProfilesCache?.Count ?? 0} profiles");
            List<FaceProfile> results;
            if (string.IsNullOrEmpty(normKeyword))
            {
                results = _allProfilesCache ?? new List<FaceProfile>();
            }
            else
            {
                results = (_allProfilesCache ?? new List<FaceProfile>()).Where(p =>
                {
                    string normName = RemoveDiacritics(p.name ?? "").ToLower();
                    string normMssv = RemoveDiacritics(p.mssv ?? "").ToLower();
                    return normName.Contains(normKeyword) || normMssv.Contains(normKeyword);
                }).ToList();
            }

            Debug.Log($"[Filter] Found {results.Count} matching profiles");

            // Manually add each item
            for (int i = 0; i < results.Count; i++)
            {
                var row = CreateStudentRow(results[i], i + 1);
                _studentListContent.Add(row);
            }
        }
    }

    // [NEW] Create student row from imported class list with registration status
    VisualElement CreateStudentRowFromStat(StudentStat stat, int stt)
    {
        var row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        row.style.alignItems = Align.Center;
        row.style.paddingLeft = 10;
        row.style.paddingRight = 10;
        row.style.paddingTop = 8;
        row.style.paddingBottom = 8;
        row.style.borderBottomWidth = 1;
        row.style.borderBottomColor = ColorFromHex("#e9ecef");
        row.style.backgroundColor = Color.white;

        // 1. STT column
        var lblStt = new Label(stt.ToString());
        lblStt.style.width = 55;
        lblStt.style.unityTextAlign = TextAnchor.MiddleCenter;
        lblStt.style.fontSize = 13;
        lblStt.style.color = ColorFromHex("#636e72");
        row.Add(lblStt);

        // 2. Name & Avatar column
        var colName = new VisualElement();
        colName.style.flexGrow = 2; 
        colName.style.flexDirection = FlexDirection.Row;
        colName.style.alignItems = Align.Center;

        var avatar = new VisualElement();
        avatar.style.width = 30;
        avatar.style.height = 30;
        avatar.style.borderTopLeftRadius = 15;
        avatar.style.borderTopRightRadius = 15;
        avatar.style.borderBottomLeftRadius = 15;
        avatar.style.borderBottomRightRadius = 15;
        avatar.style.backgroundColor = ColorFromHex("#6c5ce7");
        avatar.style.marginRight = 10;
        avatar.style.justifyContent = Justify.Center;
        avatar.style.alignItems = Align.Center;

        var avatarLetter = new Label(stat.Name?.Length > 0 ? stat.Name[0].ToString().ToUpper() : "?");
        avatarLetter.style.color = Color.white;
        avatarLetter.style.fontSize = 12;
        avatarLetter.style.unityFontStyleAndWeight = FontStyle.Bold;
        avatar.Add(avatarLetter);
        colName.Add(avatar);

        var lblName = new Label(stat.Name ?? "Unknown");
        lblName.style.fontSize = 13;
        lblName.style.color = ColorFromHex("#2d3436");
        lblName.style.unityFontStyleAndWeight = FontStyle.Bold;
        colName.Add(lblName);
        row.Add(colName);

        // 3. MSSV column
        var lblMssv = new Label(stat.MSSV ?? "");
        lblMssv.style.width = 100; // [FIX] Fixed width for alignment
        lblMssv.style.fontSize = 13;
        lblMssv.style.color = ColorFromHex("#636e72");
        lblMssv.style.unityTextAlign = TextAnchor.MiddleLeft; // [FIX] Left align
        row.Add(lblMssv);

        // 4. Status column
        var colStatus = new VisualElement();
        colStatus.style.width = 120;
        colStatus.style.alignItems = Align.Center;
        colStatus.style.justifyContent = Justify.Center;

        // [FIX] Strict check: Only MSSV matches AND doc_count > 0
        string stMssv = (stat.MSSV ?? "").Trim();
        var matchedProfile = _allProfilesCache?.FirstOrDefault(p =>
             !string.IsNullOrEmpty(p.mssv) &&
             (p.mssv ?? "").Trim().Equals(stMssv, StringComparison.OrdinalIgnoreCase) &&
             p.doc_count > 0
        );

        bool isRegistered = matchedProfile != null;
        stat.IsRegistered = isRegistered;

        var statusBadge = new Label();
        statusBadge.style.fontSize = 11;
        statusBadge.style.paddingLeft = 8;
        statusBadge.style.paddingRight = 8;
        statusBadge.style.paddingTop = 4;
        statusBadge.style.paddingBottom = 4;
        statusBadge.style.borderTopLeftRadius = 10;
        statusBadge.style.borderTopRightRadius = 10;
        statusBadge.style.borderBottomLeftRadius = 10;
        statusBadge.style.borderBottomRightRadius = 10;
        
        if (isRegistered)
        {
            statusBadge.text = "Đã đăng ký";
            statusBadge.style.backgroundColor = ColorFromHex("#d4edda");
            statusBadge.style.color = ColorFromHex("#155724");
        }
        else
        {
            statusBadge.text = "Chưa đăng ký";
            statusBadge.style.backgroundColor = ColorFromHex("#fff3cd");
            statusBadge.style.color = ColorFromHex("#856404");
        }
        colStatus.Add(statusBadge);
        row.Add(colStatus);

        // 5. Action column (Detail Button)
        var colAction = new VisualElement();
        colAction.style.width = 90;
        colAction.style.alignItems = Align.Center;
        colAction.style.justifyContent = Justify.Center;

        var btnDetail = new Button();
        btnDetail.text = "Chi tiết";
        btnDetail.style.backgroundColor = Color.clear;
        btnDetail.style.borderLeftWidth = 1;
        btnDetail.style.borderRightWidth = 1;
        btnDetail.style.borderTopWidth = 1;
        btnDetail.style.borderBottomWidth = 1;
        btnDetail.style.borderLeftColor = ColorFromHex("#dfe6e9");
        btnDetail.style.borderRightColor = ColorFromHex("#dfe6e9");
        btnDetail.style.borderTopColor = ColorFromHex("#dfe6e9");
        btnDetail.style.borderBottomColor = ColorFromHex("#dfe6e9");
        btnDetail.style.borderTopLeftRadius = 4;
        btnDetail.style.borderTopRightRadius = 4;
        btnDetail.style.borderBottomLeftRadius = 4;
        btnDetail.style.borderBottomRightRadius = 4;
        btnDetail.style.fontSize = 11;
        btnDetail.style.height = 24;
        btnDetail.style.color = ColorFromHex("#636e72");
        
        btnDetail.clicked += () =>
        {
            // Find profile if registered, otherwise create a temporary one for display
            // [FIX] Strict lookup for modal
            string stMssv = (stat.MSSV ?? "").Trim();
            var profile = _allProfilesCache?.FirstOrDefault(p =>
                (p.mssv ?? "").Trim().Equals(stMssv, StringComparison.OrdinalIgnoreCase) &&
                p.doc_count > 0
            );

            if (profile != null)
            {
                ShowStudentDetailModal(profile);
            }
            else
            {
                // Create temporary profile for unregistered students
                var tempProfile = new FaceProfile
                {
                    name = stat.Name ?? "Unknown",
                    mssv = stat.MSSV ?? ""
                };
                ShowStudentDetailModal(tempProfile);
            }
        };
        colAction.Add(btnDetail);
        row.Add(colAction);

        // Hover effect
        row.RegisterCallback<MouseEnterEvent>(evt => row.style.backgroundColor = ColorFromHex("#f8f9fa"));
        row.RegisterCallback<MouseLeaveEvent>(evt => row.style.backgroundColor = Color.white);

        return row;
    }

    VisualElement CreateStudentRow(FaceProfile profile, int stt)
    {
        var row = new VisualElement();
        row.style.flexDirection = FlexDirection.Row;
        row.style.alignItems = Align.Center;
        row.style.paddingLeft = 10;
        row.style.paddingRight = 10;
        row.style.paddingTop = 8;
        row.style.paddingBottom = 8;
        row.style.borderBottomWidth = 1;
        row.style.borderBottomColor = ColorFromHex("#e9ecef");
        row.style.backgroundColor = Color.white;

        // 1. STT column
        var lblStt = new Label(stt.ToString());
        lblStt.style.width = 55;
        lblStt.style.unityTextAlign = TextAnchor.MiddleCenter;
        lblStt.style.fontSize = 13;
        lblStt.style.color = ColorFromHex("#636e72");
        row.Add(lblStt);

        // 2. Name & Avatar column
        var colName = new VisualElement();
        colName.style.flexGrow = 2; // flex-grow 2
        colName.style.flexDirection = FlexDirection.Row;
        colName.style.alignItems = Align.Center;

        var avatar = new VisualElement();
        avatar.style.width = 30;
        avatar.style.height = 30;
        avatar.style.borderTopLeftRadius = 15;
        avatar.style.borderTopRightRadius = 15;
        avatar.style.borderBottomLeftRadius = 15;
        avatar.style.borderBottomRightRadius = 15;
        avatar.style.backgroundColor = ColorFromHex("#6c5ce7");
        avatar.style.marginRight = 10;
        avatar.style.justifyContent = Justify.Center;
        avatar.style.alignItems = Align.Center;

        var avatarLetter = new Label(profile.name?.Length > 0 ? profile.name[0].ToString().ToUpper() : "?");
        avatarLetter.style.color = Color.white;
        avatarLetter.style.fontSize = 12;
        avatarLetter.style.unityFontStyleAndWeight = FontStyle.Bold;
        avatar.Add(avatarLetter);
        colName.Add(avatar);

        var lblName = new Label(profile.name ?? "Unknown");
        lblName.style.fontSize = 13;
        lblName.style.color = ColorFromHex("#2d3436");
        lblName.style.unityFontStyleAndWeight = FontStyle.Bold;
        colName.Add(lblName);
        row.Add(colName);
        
        // 3. MSSV column
        var lblMssv = new Label(profile.mssv ?? "");
        lblMssv.style.flexGrow = 1;
        lblMssv.style.fontSize = 13;
        lblMssv.style.color = ColorFromHex("#636e72");
        row.Add(lblMssv);

        // 4. Status column (Always Registered since it comes from profile list)
        var colStatus = new VisualElement();
        colStatus.style.width = 120;
        colStatus.style.alignItems = Align.Center;
        colStatus.style.justifyContent = Justify.Center;

        var statusBadge = new Label();
        statusBadge.text = "Đã đăng ký";
        statusBadge.style.fontSize = 11;
        statusBadge.style.paddingLeft = 8;
        statusBadge.style.paddingRight = 8;
        statusBadge.style.paddingTop = 4;
        statusBadge.style.paddingBottom = 4;
        statusBadge.style.borderTopLeftRadius = 10;
        statusBadge.style.borderTopRightRadius = 10;
        statusBadge.style.borderBottomLeftRadius = 10;
        statusBadge.style.borderBottomRightRadius = 10;
        statusBadge.style.backgroundColor = ColorFromHex("#d4edda");
        statusBadge.style.color = ColorFromHex("#155724");
        
        colStatus.Add(statusBadge);
        row.Add(colStatus);

        // 5. Action column
        var colAction = new VisualElement();
        colAction.style.width = 90;
        colAction.style.alignItems = Align.Center;
        colAction.style.justifyContent = Justify.Center;

        var btnDetail = new Button();
        btnDetail.text = "Chi tiết";
        btnDetail.style.backgroundColor = Color.clear;
        btnDetail.style.borderLeftWidth = 1;
        btnDetail.style.borderRightWidth = 1;
        btnDetail.style.borderTopWidth = 1;
        btnDetail.style.borderBottomWidth = 1;
        btnDetail.style.borderLeftColor = ColorFromHex("#dfe6e9");
        btnDetail.style.borderRightColor = ColorFromHex("#dfe6e9");
        btnDetail.style.borderTopColor = ColorFromHex("#dfe6e9");
        btnDetail.style.borderBottomColor = ColorFromHex("#dfe6e9");
        btnDetail.style.borderTopLeftRadius = 4;
        btnDetail.style.borderTopRightRadius = 4;
        btnDetail.style.borderBottomLeftRadius = 4;
        btnDetail.style.borderBottomRightRadius = 4;
        btnDetail.style.fontSize = 11;
        btnDetail.style.height = 24;
        btnDetail.style.color = ColorFromHex("#636e72");
        
        btnDetail.clicked += () => ShowStudentDetailModal(profile);
        
        colAction.Add(btnDetail);
        row.Add(colAction);

        // Hover effect
        row.RegisterCallback<MouseEnterEvent>(evt => row.style.backgroundColor = ColorFromHex("#f8f9fa"));
        row.RegisterCallback<MouseLeaveEvent>(evt => row.style.backgroundColor = Color.white);

        return row;
    }

    void ShowStudentDetailModal(FaceProfile profile)
    {
        // Use the original template if available
        if (modalStudentDetailTemplate == null)
        {
            Debug.LogWarning("[StudentDetail] modalStudentDetailTemplate is not assigned in Inspector!");
            ShowToast("⚠️ Template chi tiết chưa được gán!", colOrange);
            return;
        }

        // Clone template
        var modal = modalStudentDetailTemplate.CloneTree();
        var overlay = modal.Q<VisualElement>("ModalOverlay");
        if (overlay == null)
        {
            Debug.LogError("[StudentDetail] ModalOverlay not found in template!");
            return;
        }

        overlay.style.display = DisplayStyle.Flex;

        // [FIX] Add explicit positioning to cover full screen
        overlay.style.position = Position.Absolute;
        overlay.style.top = 0;
        overlay.style.bottom = 0;
        overlay.style.left = 0;
        overlay.style.right = 0;
        overlay.style.backgroundColor = new Color(0, 0, 0, 0.7f);
        overlay.style.justifyContent = Justify.Center;
        overlay.style.alignItems = Align.Center;

        // Bind data
        var lblName = modal.Q<Label>("ModalName");
        var lblMssv = modal.Q<Label>("ModalMssv");
        var lblTotal = modal.Q<Label>("ModalTotal");
        var lblLate = modal.Q<Label>("ModalLate");
        var lblAbsent = modal.Q<Label>("ModalAbsent");
        var avatarChar = modal.Q<Label>("ModalAvatarChar");
        var avatarImage = modal.Q<VisualElement>("ModalAvatarImage");
        var lblWarnLate = modal.Q<Label>("LblWarnLate");
        var lblWarnAbsent = modal.Q<Label>("LblWarnAbsent");

        if (lblName != null) lblName.text = profile.name ?? "Unknown";
        if (lblMssv != null) lblMssv.text = $"MSSV: {profile.mssv ?? "N/A"}";

        // Calculate real stats from _allLogs
        int totalCount = 0;
        int lateCount = 0;
        int absentCount = 0; // Placeholder - would need session data to calculate

        if (_allLogs != null && !string.IsNullOrEmpty(profile.mssv))
        {
            var studentLogs = _allLogs.Where(l => l.mssv == profile.mssv).ToList();
            totalCount = studentLogs.Count;
            lateCount = studentLogs.Count(l => l.status?.ToLower() == "late" || l.status?.ToLower() == "trễ");
        }

        if (lblTotal != null) lblTotal.text = totalCount.ToString();
        if (lblLate != null) lblLate.text = lateCount.ToString();
        if (lblAbsent != null) lblAbsent.text = absentCount.ToString();

        // Show warning badges
        if (lblWarnLate != null)
        {
            if (lateCount > 3)
            {
                lblWarnLate.style.display = DisplayStyle.Flex;
                lblWarnLate.text = $"⚠ Báo động (Trễ > 3)";
            }
            else
            {
                lblWarnLate.style.display = DisplayStyle.None;
            }
        }

        if (lblWarnAbsent != null)
        {
            if (absentCount >= 3)
            {
                lblWarnAbsent.style.display = DisplayStyle.Flex;
            }
            else
            {
                lblWarnAbsent.style.display = DisplayStyle.None;
            }
        }

        // Avatar letter (fallback)
        if (avatarChar != null)
        {
            avatarChar.text = profile.name?.Length > 0 ? profile.name[0].ToString().ToUpper() : "?";
        }

        // Load face image from Firebase Storage (same as Stats popup)
        if (avatarImage != null && !string.IsNullOrEmpty(profile.name) && !string.IsNullOrEmpty(profile.mssv))
        {
            avatarImage.style.display = DisplayStyle.None;
            // ✅ FIX: Use EnsureAvatarLoaded for reliable avatar loading with signed URL fallback
            StartCoroutine(EnsureAvatarLoaded(profile.name, profile.mssv, avatarImage, avatarChar, profile.prefix));
        }

        // Close button
        var btnClose = modal.Q<Button>("BtnCloseModal");
        if (btnClose != null)
        {
            btnClose.clicked += () => _root.Remove(overlay);
        }

        // Click overlay to close
        overlay.RegisterCallback<ClickEvent>(evt =>
        {
            if (evt.target == overlay) _root.Remove(overlay);
        });

        // Delete button
        var btnDelete = modal.Q<Button>("BtnDeleteStudent");
        if (btnDelete != null)
        {
            btnDelete.clicked += () =>
            {
                if (btnDelete.text != "Chắc chắn?")
                {
                    btnDelete.text = "Chắc chắn?";
                    btnDelete.schedule.Execute(() =>
                    {
                        if (btnDelete.text == "Chắc chắn?") btnDelete.text = "Xóa sinh viên";
                    }).StartingIn(3000);
                    return;
                }
                btnDelete.text = "Đang xóa...";
                btnDelete.SetEnabled(false);
                // [FIX] Perform full delete (delete_roster = true)
                StartCoroutine(DeleteProfile(profile.profile_key, true, (ok) =>
                {
                    if (ok)
                    {
                        _root.Remove(overlay);
                        ShowToast("Đã xóa: " + profile.name, colRed);

                        // [NEW] Local Cleanup for Class List Tab
                        string m = (profile.mssv ?? "").Trim();
                        if (!string.IsNullOrEmpty(m) && _studentDataList != null)
                        {
                            _studentDataList.RemoveAll(s => (s.MSSV ?? "").Trim() == m);
                            SaveStudentDataListToFile();
                            // If PushRosterToServer exists, it would go here. For now local save implies list update.
                        }
                        
                        // [NEW] Refresh cache
                        StartCoroutine(GetProfiles(p =>
                        {
                            _allProfilesCache = p?.ToList() ?? new List<FaceProfile>();
                            _skipNextProfilesFetchInRegisterTab = true;

                            RenderRegisterTabContent();
                            FilterStudentList(_inpSearchStudent?.value ?? "");
                            UpdateStatsFromLogs();
                            UpdateTrendChart();
                            UpdateStatsTab();
                        }));
                    }
                    else
                    {
                        btnDelete.text = "Lỗi!";
                        btnDelete.SetEnabled(true);
                    }
                }));
            };
        }

        _root.Add(overlay); // Add overlay directly, not the template container
    }

    void ShowAbsentStudentsPopup()
    {
        // ✅ FIX #8: Load roster thay vì nghĩ FetchAndRenderStudentList() sẽ có _studentDataList
        if (_studentDataList == null || _studentDataList.Count == 0)
            LoadStudentDataListFromFile();

        // Nếu vẫn chưa có roster -> báo
        if (_studentDataList == null || _studentDataList.Count == 0)
        {
            ShowToast("⚠️ Chưa có danh sách lớp (hãy import danh sách)!", colOrange);
            ShowAbsentStudentsPopupInternal(); // sẽ hiện popup rỗng
            return;
        }

        ShowAbsentStudentsPopupInternal();
    }

    void ShowAbsentStudentsPopupInternal()
    {
        // ✅ Đảm bảo roster có dữ liệu
        if (_studentDataList == null) _studentDataList = new List<StudentStat>();
        if (_studentDataList.Count == 0) LoadStudentDataListFromFile();

        List<StudentStat> absentList = new List<StudentStat>();

        // Ngày đang xem trên Home (DrpDaySelector)
        DateTime selectedDate = GetSelectedHomeDate();
        string selectedDateStr = selectedDate.ToString("dd/MM/yyyy");

        // Nếu ngày đó không có học hoặc CHƯA kết thúc buổi -> không kết luận vắng
        if (!HasClass(selectedDate) || !HasClassFinished(selectedDate))
        {
            absentList = new List<StudentStat>();
        }
        else
        {
            // MSSV đã có log trong ngày selected (không tính log có status = absent)
            var presentMssvs = new HashSet<string>();
            if (_allLogs != null)
            {
                foreach (var l in _allLogs)
                {
                    if (l.date == selectedDateStr && !string.IsNullOrEmpty(l.mssv) && !IsAbsentStatusHelper(l.status))
                        presentMssvs.Add(l.mssv.Trim());
                }
            }

            // Ai trong roster mà không có log ngày đó -> vắng ngày đó
            absentList = new List<StudentStat>();
            foreach (var s in _studentDataList)
            {
                string m = (s.MSSV ?? "").Trim();
                if (m.Length == 0) continue;

                if (!presentMssvs.Contains(m))
                    absentList.Add(new StudentStat { Name = s.Name, MSSV = s.MSSV });
            }

            // Sort by name
            absentList = absentList.OrderBy(x => x.Name).ToList();
        }

        // Create overlay
        var overlay = new VisualElement();
        overlay.style.position = Position.Absolute;
        overlay.style.top = 0; overlay.style.bottom = 0;
        overlay.style.left = 0; overlay.style.right = 0;
        overlay.style.backgroundColor = new Color(0, 0, 0, 0.7f);
        overlay.style.justifyContent = Justify.Center;
        overlay.style.alignItems = Align.Center;
        overlay.RegisterCallback<ClickEvent>(e => { if (e.target == overlay) _root.Remove(overlay); });

        // Modal card
        var card = new VisualElement();
        card.style.width = 400;
        card.style.maxHeight = 500;
        card.style.backgroundColor = Color.white;
        card.style.borderTopLeftRadius = 16; card.style.borderTopRightRadius = 16;
        card.style.borderBottomLeftRadius = 16; card.style.borderBottomRightRadius = 16;
        card.style.paddingTop = 20; card.style.paddingBottom = 20;
        card.style.paddingLeft = 20; card.style.paddingRight = 20;
        overlay.Add(card);

        // Header
        var header = new VisualElement();
        header.style.flexDirection = FlexDirection.Row;
        header.style.justifyContent = Justify.SpaceBetween;
        header.style.alignItems = Align.Center;
        header.style.marginBottom = 15;

        var title = new Label($"⚠️ Danh sách vắng ({absentList.Count} sinh viên)");
        title.style.fontSize = 18;
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        title.style.color = ColorFromHex("#e17055");
        header.Add(title);

        var closeBtn = new Button { text = "✕" };
        closeBtn.style.backgroundColor = Color.clear;
        closeBtn.style.fontSize = 18;
        closeBtn.style.color = ColorFromHex("#636e72");
        SetBorderWidth(closeBtn, 0);
        closeBtn.clicked += () => _root.Remove(overlay);
        header.Add(closeBtn);
        card.Add(header);

        // Scroll view for list
        var scrollView = new ScrollView();
        scrollView.style.flexGrow = 1;
        scrollView.style.maxHeight = 350;

        if (absentList.Count == 0)
        {
            var emptyLabel = new Label("🎉 Không có sinh viên nào vắng!");
            emptyLabel.style.fontSize = 14;
            emptyLabel.style.color = ColorFromHex("#00b894");
            emptyLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            emptyLabel.style.paddingTop = 30;
            emptyLabel.style.paddingBottom = 30;
            scrollView.Add(emptyLabel);
        }
        else
        {
            foreach (var student in absentList)
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.paddingTop = 10;
                row.style.paddingBottom = 10;
                row.style.borderBottomWidth = 1;
                row.style.borderBottomColor = ColorFromHex("#e9ecef");

                // Avatar
                var avatar = new VisualElement();
                avatar.style.width = 36;
                avatar.style.height = 36;
                avatar.style.borderTopLeftRadius = 18; avatar.style.borderTopRightRadius = 18;
                avatar.style.borderBottomLeftRadius = 18; avatar.style.borderBottomRightRadius = 18;
                avatar.style.backgroundColor = ColorFromHex("#e17055");
                avatar.style.justifyContent = Justify.Center;
                avatar.style.alignItems = Align.Center;
                avatar.style.marginRight = 12;

                var avatarLetter = new Label(student.Name?.Length > 0 ? student.Name[0].ToString().ToUpper() : "?");
                avatarLetter.style.color = Color.white;
                avatarLetter.style.fontSize = 14;
                avatarLetter.style.unityFontStyleAndWeight = FontStyle.Bold;
                avatar.Add(avatarLetter);
                row.Add(avatar);

                // Info
                var info = new VisualElement();
                info.style.flexGrow = 1;

                var lblName = new Label(student.Name ?? "Unknown");
                lblName.style.fontSize = 14;
                lblName.style.unityFontStyleAndWeight = FontStyle.Bold;
                lblName.style.color = ColorFromHex("#2d3436");
                info.Add(lblName);

                var lblMssv = new Label(student.MSSV ?? "");
                lblMssv.style.fontSize = 11;
                lblMssv.style.color = ColorFromHex("#636e72");
                info.Add(lblMssv);
                row.Add(info);

                // Absent count badge
                var badge = new Label($"{student.AbsentCount} buổi");
                badge.style.backgroundColor = ColorFromHex("#ffe6e0");
                badge.style.color = ColorFromHex("#e17055");
                badge.style.fontSize = 11;
                badge.style.unityFontStyleAndWeight = FontStyle.Bold;
                badge.style.paddingTop = 4; badge.style.paddingBottom = 4;
                badge.style.paddingLeft = 8; badge.style.paddingRight = 8;
                badge.style.borderTopLeftRadius = 10; badge.style.borderTopRightRadius = 10;
                badge.style.borderBottomLeftRadius = 10; badge.style.borderBottomRightRadius = 10;
                row.Add(badge);

                // Click to show detail
                row.RegisterCallback<ClickEvent>(evt =>
                {
                    _root.Remove(overlay);
                    ShowStudentDetail(student);
                });
                row.RegisterCallback<MouseEnterEvent>(evt => row.style.backgroundColor = ColorFromHex("#f8f9fa"));
                row.RegisterCallback<MouseLeaveEvent>(evt => row.style.backgroundColor = Color.clear);

                scrollView.Add(row);
            }
        }

        card.Add(scrollView);
        _root.Add(overlay);
    }

    IEnumerator LoadStudentFaceImage(string mssv, System.Action<Texture2D> onComplete)
    {
        // Try to load face image from Firebase Storage
        // Pattern: faces/{mssv}/face_center.jpg or similar
        string baseUrl = "https://firebasestorage.googleapis.com/v0/b/phamgiahuy-9ab1c.firebasestorage.app/o/faces%2F";
        string imagePath = $"{mssv}%2Fface_center.jpg?alt=media";
        string fullUrl = baseUrl + imagePath;

        Debug.Log($"[FaceImage] Loading: {fullUrl}");

        using (var req = UnityWebRequestTexture.GetTexture(fullUrl))
        {
            req.timeout = 10;
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                var texture = DownloadHandlerTexture.GetContent(req);
                Debug.Log($"[FaceImage] Loaded successfully for {mssv}");
                onComplete?.Invoke(texture);
            }
            else
            {
                Debug.LogWarning($"[FaceImage] Failed to load for {mssv}: {req.error}");
                onComplete?.Invoke(null);
            }
        }
    }

    public static string RemoveDiacritics(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        string[] signs = new string[] {
            "aAeEoOuUiIdDyY",
            "áàạảãâấầậẩẫăắằặẳẵ",
            "ÁÀẠẢÃÂẤẦẬẨẪĂẮẰẶẲẴ",
            "éèẹẻẽêếềệểễ",
            "ÉÈẸẺẼÊẾỀỆỂỄ",
            "óòọỏõôốồộổỗơớờợởỡ",
            "ÓÒỌỎÕÔỐỒỘỔỖƠỚỜỢỞỠ",
            "úùụủũưứừựửữ",
            "ÚÙỤỦŨƯỨỪỰỬỮ",
            "íìịỉĩ",
            "ÍÌỊỈĨ",
            "đ",
            "Đ",
            "ýỳỵỷỹ",
            "ÝỲỴỶỸ"
        };
        for (int i = 1; i < signs.Length; i++)
        {
            for (int j = 0; j < signs[i].Length; j++)
            {
                text = text.Replace(signs[i][j], signs[0][i - 1]);
            }
        }
        return text;
    }

    // =========================================================
    // HÀM SETUP REGISTER TAB (Standalone)
    // =========================================================
    void SetupRegisterTab()
    {
        var btnRegister = _uiRoot.Q<Button>("BtnRegister"); // Icon hình người trong Sidebar
        _registerTabContainer = _uiRoot.Q<VisualElement>("RegisterContainer");

        // Nếu UXML chưa có Container, tạo mới và gắn vào MainContent
        if (_registerTabContainer == null)
        {
            _registerTabContainer = new VisualElement();
            _registerTabContainer.name = "RegisterContainer";
            _registerTabContainer.style.display = DisplayStyle.None;
            _registerTabContainer.style.flexGrow = 1;
            _registerTabContainer.style.paddingLeft = 30; _registerTabContainer.style.paddingRight = 30;
            _registerTabContainer.style.paddingTop = 30; _registerTabContainer.style.paddingBottom = 30;

            // Add to parent of other containers
            var mainParent = _uiRoot.Q<VisualElement>("MainContentContainer")?.parent ?? _uiRoot;
            mainParent.Add(_registerTabContainer);
        }

        if (btnRegister != null)
        {
            btnRegister.clicked += SwitchToRegisterTab;
        }
    }

    void SwitchToRegisterTab()
    {
        var btnRegister = _uiRoot.Q<Button>("BtnRegister");
        var btnHome = _uiRoot.Q<Button>("BtnHome");
        var btnClassList = _uiRoot.Q<Button>("BtnClassList");
        var btnStats = _uiRoot.Q<Button>("BtnStats");
        var btnSchedule = _uiRoot.Q<Button>("BtnSchedule");
        var btnSettings = _uiRoot.Q<Button>("BtnSettings");
        var btnAttendance = _uiRoot.Q<Button>("BtnAttendance");

        if (btnHome != null) btnHome.RemoveFromClassList("active");
        if (btnClassList != null) btnClassList.RemoveFromClassList("active");
        if (btnStats != null) btnStats.RemoveFromClassList("active");
        if (btnSchedule != null) btnSchedule.RemoveFromClassList("active");
        if (btnSettings != null) btnSettings.RemoveFromClassList("active");
        if (btnAttendance != null) btnAttendance.RemoveFromClassList("active");
        if (btnRegister != null) btnRegister.AddToClassList("active");

        var mainContent = _uiRoot.Q<VisualElement>("MainContentContainer");
        var history = _uiRoot.Q<VisualElement>("HistoryContainer");
        var studentList = _uiRoot.Q<VisualElement>("StudentListContainer");
        var stats = _uiRoot.Q<VisualElement>("StatsContainer");
        var schedule = _uiRoot.Q<VisualElement>("ScheduleContainer");
        var settings = _uiRoot.Q<VisualElement>("SettingsContainer");
        var attendance = _uiRoot.Q<VisualElement>("AttendanceContainer");
        var cam = _uiRoot.Q<VisualElement>("CameraPreview");

        if (mainContent != null) mainContent.style.display = DisplayStyle.None;
        if (history != null) history.style.display = DisplayStyle.None;
        if (studentList != null) studentList.style.display = DisplayStyle.None;
        if (stats != null) stats.style.display = DisplayStyle.None;
        if (schedule != null) schedule.style.display = DisplayStyle.None;
        if (settings != null) settings.style.display = DisplayStyle.None;
        if (attendance != null) attendance.style.display = DisplayStyle.None;
        if (cam != null) cam.style.display = DisplayStyle.None;

        if (_registerTabContainer != null)
        {
            _registerTabContainer.style.display = DisplayStyle.Flex;
            RenderRegisterTabContent();
            AnimateEntry(_registerTabContainer);
        }
    }

    void RenderRegisterTabContent()
    {
        _registerTabContainer.Clear();

        // [NEW] Load saved student list if not already loaded
        if (_studentDataList == null || _studentDataList.Count == 0)
        {
            LoadStudentDataListFromFile();
        }

        // --- HEADER ROW ---
        var headerRow = new VisualElement();
        headerRow.style.flexDirection = FlexDirection.Row;
        headerRow.style.justifyContent = Justify.SpaceBetween;
        headerRow.style.alignItems = Align.Center;
        headerRow.style.marginBottom = 20;
        _registerTabContainer.Add(headerRow);

        var title = new Label("ĐĂNG KÝ KHUÔN MẶT MỚI");
        title.style.fontSize = 24;
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        title.style.color = colTextDark;
        headerRow.Add(title);

        var btnStart = new Button();
        btnStart.text = "📸 BẮT ĐẦU CHỤP";
        btnStart.style.height = 45;
        btnStart.style.paddingLeft = 25; btnStart.style.paddingRight = 25;
        btnStart.style.backgroundColor = colAccent;
        btnStart.style.color = Color.white;
        btnStart.style.fontSize = 14;
        btnStart.style.unityFontStyleAndWeight = FontStyle.Bold;
        btnStart.style.borderTopLeftRadius = 8; btnStart.style.borderTopRightRadius = 8;
        btnStart.style.borderBottomLeftRadius = 8; btnStart.style.borderBottomRightRadius = 8;
        headerRow.Add(btnStart);

        // --- STUDENT LIST AREA (White Box) ---
        var listContainer = new VisualElement();
        listContainer.style.flexGrow = 1;
        listContainer.style.backgroundColor = Color.white;
        listContainer.style.borderTopLeftRadius = 10; listContainer.style.borderTopRightRadius = 10;
        listContainer.style.borderBottomLeftRadius = 10; listContainer.style.borderBottomRightRadius = 10;
        listContainer.style.paddingLeft = 20; listContainer.style.paddingRight = 20;
        listContainer.style.paddingTop = 20; listContainer.style.paddingBottom = 20;
        _registerTabContainer.Add(listContainer);

        var listTitle = new Label("Danh sách lớp - Trạng thái đăng ký");
        listTitle.style.fontSize = 16;
        listTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
        listTitle.style.color = colTextDark;
        listTitle.style.marginBottom = 15;
        listContainer.Add(listTitle);

        var scrollView = new ScrollView();
        scrollView.style.flexGrow = 1;
        listContainer.Add(scrollView);

        // --- FETCH AND RENDER STUDENTS ---
        StartCoroutine(FetchAndRenderRegisteredStudents(scrollView));

        // --- CLICK: START CAPTURE ---
        btnStart.clicked += () =>
        {
            try
            {
                Debug.Log("[RegisterTab] Starting Capture...");

                if (_modalContainer == null)
                {
                    Debug.LogError("_modalContainer is null!");
                    ShowToast("Lỗi UI: ModalContainer null", colRed);
                    return;
                }

                // Clear previous inputs
                _regName = "";
                _regMssv = "";

                _modalContainer.style.display = DisplayStyle.Flex;
                var overlay = new VisualElement();
                overlay.style.position = Position.Absolute;
                overlay.style.left = 0; overlay.style.top = 0; overlay.style.right = 0; overlay.style.bottom = 0;
                overlay.style.backgroundColor = new Color(0, 0, 0, 0.8f);
                overlay.style.alignItems = Align.Center;
                overlay.style.justifyContent = Justify.Center;
                _modalContainer.Add(overlay);

                var box = new VisualElement();
                box.style.backgroundColor = Color.white;
                box.style.width = 500;
                box.style.borderTopLeftRadius = 12; box.style.borderTopRightRadius = 12;
                box.style.borderBottomLeftRadius = 12; box.style.borderBottomRightRadius = 12;
                box.style.paddingLeft = 20; box.style.paddingRight = 20;
                box.style.paddingTop = 20; box.style.paddingBottom = 20;
                overlay.Add(box);

                // Close Button
                var btnClose = new Button();
                btnClose.text = "✖";
                btnClose.style.position = Position.Absolute;
                btnClose.style.top = 10; btnClose.style.right = 10;
                btnClose.style.width = 30; btnClose.style.height = 30;
                btnClose.style.backgroundColor = Color.clear;
                btnClose.style.color = colTextDark;
                btnClose.style.borderTopWidth = 0; btnClose.style.borderBottomWidth = 0;
                btnClose.style.borderLeftWidth = 0; btnClose.style.borderRightWidth = 0;
                btnClose.style.fontSize = 18;
                btnClose.style.unityFontStyleAndWeight = FontStyle.Bold;
                btnClose.clicked += () =>
                {
                    Debug.Log("[RegisterTab] User Cancelled.");
                    if (_trainingCoroutine != null) StopCoroutine(_trainingCoroutine);
                    overlay.RemoveFromHierarchy();
                    _modalContainer.style.display = DisplayStyle.None;
                    SetCameraDriver(false);
                };
                box.Add(btnClose);

                // --- STEP 0: INPUT INFO ---
                var lblHeader = new Label("NHẬP THÔNG TIN");
                lblHeader.style.fontSize = 20; 
                lblHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
                lblHeader.style.marginBottom = 15;
                lblHeader.style.color = colTextDark;
                lblHeader.style.unityTextAlign = TextAnchor.MiddleCenter;
                box.Add(lblHeader);

                var inpMssv = new TextField("MSSV:");
                inpMssv.style.marginBottom = 10;
                box.Add(inpMssv);

                var inpName = new TextField("Họ tên:");
                inpName.style.marginBottom = 20;
                box.Add(inpName);

                var btnNext = new Button();
                btnNext.text = "TIẾP TỤC ➡";
                btnNext.style.height = 40;
                btnNext.style.backgroundColor = colAccent;
                btnNext.style.color = Color.white;
                btnNext.style.unityFontStyleAndWeight = FontStyle.Bold;
                box.Add(btnNext);

                btnNext.clicked += () => 
                {
                    string m = (inpMssv.value ?? "").Trim();
                    string n = (inpName.value ?? "").Trim();

                    if (string.IsNullOrEmpty(m)) { ShowToast("Vui lòng nhập MSSV!", colRed); return; }
                    if (string.IsNullOrEmpty(n)) { ShowToast("Vui lòng nhập Họ tên!", colRed); return; }

                    _regMssv = m;
                    _regName = n;

                    // Clear Input UI -> Start ProcessTrainingFlow
                    box.Clear();
                    
                    // Start Camera
                    Debug.Log("[RegisterTab] Starting ProcessTrainingFlow...");
                    if (_cameraMainContainer != null) _cameraMainContainer.style.display = DisplayStyle.None;
                    SetCameraDriver(true);
                    
                    _trainingCoroutine = StartCoroutine(ProcessTrainingFlow(overlay, box, false));
                };
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[RegisterTab] Error: {ex}");
                ShowToast($"Lỗi: {ex.Message}", colRed);
            }
        };
    }

    // --- HELPER: Fetch & Render Registered Students for Register Tab ---
    IEnumerator FetchAndRenderRegisteredStudents(VisualElement container)
    {
        container.Clear();

        var loading = new Label("⏳ Đang tải...");
        loading.style.color = colTextLight;
        container.Add(loading);

        // ✅ [OPT] Check if we can skip fetch and reuse cache
        if (_skipNextProfilesFetchInRegisterTab && _allProfilesCache != null)
        {
            _skipNextProfilesFetchInRegisterTab = false; // consume flag
            container.Clear();
            Debug.Log($"[RegisterTab] Reusing cached {_allProfilesCache.Count} profiles (skip fetch)");
        }
        else
        {
            // Fetch profiles from API
            string url = $"{cloudRunApiUrl}/profiles?teacher_id={_currentTeacherId}";
            using (var req = UnityWebRequest.Get(url))
            {
                ApplyTenantHeaders(req);
                req.timeout = 120;
                yield return req.SendWebRequest();

                container.Clear();

                // Cache profiles for status checking
                if (req.result == UnityWebRequest.Result.Success)
                {
                    var resp = JsonUtility.FromJson<ProfilesResponse>(req.downloadHandler.text);
                    _allProfilesCache = resp.profiles?.ToList() ?? new List<FaceProfile>();
                    Debug.Log($"[RegisterTab] Cached {_allProfilesCache.Count} registered profiles");
                }
                else
                {
                    Debug.LogWarning($"[RegisterTab] Failed to fetch profiles: {req.error}");
                    _allProfilesCache = new List<FaceProfile>();
                }
            }
        }

        // Build dictionary for fast lookup by MSSV (only profiles with doc_count > 0)
        var profilesByMssv = _allProfilesCache
            .Where(p => !string.IsNullOrWhiteSpace(p.mssv) && p.doc_count > 0)
            .GroupBy(p => p.mssv.Trim())
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.doc_count).First());

        // Check if we have imported class list
        if (_studentDataList == null || _studentDataList.Count == 0)
        {
            var empty = new Label("📋 Chưa import danh sách lớp.\nHãy nhập danh sách từ file Excel trước.");
            empty.style.color = colTextLight;
            empty.style.marginTop = 20;
            empty.style.whiteSpace = WhiteSpace.Normal;
            container.Add(empty);
            yield break;
        }

        // Render imported class list with registration status
        foreach (var stat in _studentDataList)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingTop = 8; row.style.paddingBottom = 8;
            row.style.borderBottomWidth = 1;
            row.style.borderBottomColor = new Color(0.9f, 0.9f, 0.9f);

            // Avatar Placeholder
            var avatar = new Label(stat.Name?.Length > 0 ? stat.Name[0].ToString().ToUpper() : "?");
            avatar.style.width = 40; avatar.style.height = 40;
            avatar.style.borderTopLeftRadius = 20; avatar.style.borderTopRightRadius = 20;
            avatar.style.borderBottomLeftRadius = 20; avatar.style.borderBottomRightRadius = 20;
            avatar.style.backgroundColor = colAccent;
            avatar.style.color = Color.white;
            avatar.style.unityTextAlign = TextAnchor.MiddleCenter;
            avatar.style.fontSize = 18;
            avatar.style.marginRight = 15;
            row.Add(avatar);

            // Info
            var info = new VisualElement();
            info.style.flexGrow = 1;
            var lblName = new Label(stat.Name ?? "N/A");
            lblName.style.fontSize = 14;
            lblName.style.unityFontStyleAndWeight = FontStyle.Bold;
            lblName.style.color = colTextDark;
            info.Add(lblName);
            var lblMssv = new Label(stat.MSSV ?? "");
            lblMssv.style.fontSize = 12;
            lblMssv.style.color = colTextLight;
            info.Add(lblMssv);
            row.Add(info);

            // [NEW] Registration Status Badge - use profilesByMssv (already filtered by doc_count > 0)
            string statMssv = (stat.MSSV ?? "").Trim();

            FaceProfile prof = null;
            bool hasProfile = !string.IsNullOrWhiteSpace(statMssv) && profilesByMssv.TryGetValue(statMssv, out prof);

            // ✅ CHỈ coi là đã đăng ký khi có profile VÀ doc_count > 0 (đã filter trong dictionary)
            bool isRegistered = hasProfile && prof != null;
            stat.IsRegistered = isRegistered;

            var statusBadge = new Label();
            statusBadge.style.fontSize = 11;
            statusBadge.style.paddingLeft = 8;
            statusBadge.style.paddingRight = 8;
            statusBadge.style.paddingTop = 4;
            statusBadge.style.paddingBottom = 4;
            statusBadge.style.borderTopLeftRadius = 10;
            statusBadge.style.borderTopRightRadius = 10;
            statusBadge.style.borderBottomLeftRadius = 10;
            statusBadge.style.borderBottomRightRadius = 10;
            statusBadge.style.marginRight = 10;

            if (isRegistered)
            {
                statusBadge.text = "✅ Đã đăng ký";
                statusBadge.style.backgroundColor = ColorFromHex("#d4edda");
                statusBadge.style.color = ColorFromHex("#155724");
            }
            else
            {
                statusBadge.text = "⚠ Chưa đăng ký";
                statusBadge.style.backgroundColor = ColorFromHex("#fff3cd");
                statusBadge.style.color = ColorFromHex("#856404");
            }
            row.Add(statusBadge);

            // Keep in-memory status in sync
            stat.IsRegistered = isRegistered;

            // Click row to register when student is not registered yet
            if (!isRegistered)
            {
                row.tooltip = "Nhấn để đăng ký khuôn mặt";

                row.RegisterCallback<MouseEnterEvent>(_ => row.style.backgroundColor = ColorFromHex("#f8f9fa"));
                row.RegisterCallback<MouseLeaveEvent>(_ => row.style.backgroundColor = Color.clear);

                row.RegisterCallback<ClickEvent>(_ =>
                {
                    // Prefill name/mssv
                    _regName = stat.Name ?? "";
                    _regMssv = stat.MSSV ?? "";

                    // Open existing capture/register popup
                    OnAddFaceClicked();
                });
            }

            // Delete Button (only for registered profiles)
            if (isRegistered)
            {
                var btnDel = new Button();
                btnDel.text = "🗑️";
                btnDel.style.backgroundColor = Color.clear;
                btnDel.style.fontSize = 16;
                btnDel.style.borderTopWidth = 0; btnDel.style.borderBottomWidth = 0;
                btnDel.style.borderLeftWidth = 0; btnDel.style.borderRightWidth = 0;

                // Find profile key to delete
                var profile = _allProfilesCache?.FirstOrDefault(p => p.mssv == stat.MSSV);
                string profileKeyToDelete = profile?.profile_key ?? "";

                if (!string.IsNullOrEmpty(profileKeyToDelete))
                {
                    btnDel.clicked += () =>
                    {
                        StartCoroutine(DeleteProfileAndRefresh(profileKeyToDelete));
                    };
                    row.Add(btnDel);
                }
            }

            container.Add(row);
        }
    }

    // --- HELPER: Delete Profile and Refresh All Related UIs ---
    IEnumerator DeleteProfileAndRefresh(string profileKey)
    {
        ShowToast($"Đang xóa...", colOrange);

        string url = $"{cloudRunApiUrl}/delete_profile";
        // [FIX] Use DeleteProfileReq with delete_roster=true
        var payload = JsonUtility.ToJson(new DeleteProfileReq { profile_key = profileKey, delete_roster = true });

        using (var req = new UnityWebRequest(url, "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(payload);
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            ApplyTenantHeaders(req);
            req.timeout = 15;
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                ShowToast($"✅ Đã xóa thành công!", colGreen);

                // Refresh profiles cache trước -> avatar/status cập nhật ngay
                FaceProfile[] profiles = null;
                yield return StartCoroutine(GetProfiles(p => profiles = p));
                _allProfilesCache = profiles?.ToList() ?? new List<FaceProfile>();

                _skipNextProfilesFetchInRegisterTab = true;

                RenderRegisterTabContent();
                FilterStudentList(_inpSearchStudent?.value ?? "");
                UpdateStatsFromLogs();
                UpdateTrendChart();
                UpdateStatsTab();
            }
            else
            {
                ShowToast($"❌ Xóa thất bại: {req.error}", colRed);
            }
        }
    }


    Color ColorFromHex(string hex)
    {
        if (hex.StartsWith("#")) hex = hex.Substring(1);
        if (hex.Length == 6)
        {
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            return new Color(r / 255f, g / 255f, b / 255f);
        }
        return Color.white;

    }


    // [NEW] Delete Profile Request Struct
    [System.Serializable]
    private class DeleteProfileReq
    {
        public string profile_key;
        public bool delete_roster;
    }

    // [NEW] Change Password Request Struct
    [System.Serializable]
    private class ChangePassReq
    {
        public string username;
        public string old_password;
        public string new_password;
    }
    // ====== CLASS SWITCHER UI ======
    private VisualElement _cardClassSwitcher;
    private Label _lblSwitcherSub;

    // [NEW] Stats tab class bar
    private Button _btnStatsChangeClass;
    private Label _lblStatsSelectedClass;

    // [NEW] Home card label
    private Label _lblCurrentClass;

    // Class Picker Modal (UXML-based)
    private VisualElement _classPickerOverlay;
    private VisualElement _classPickerCard;
    private ScrollView _classPickerList;
    private Button _btnClassPickerClose;

    private struct ClassOption
    {
        public string classCode;    // ví dụ: COOL325364_06
        public string subjectName;  // ví dụ: Xử lý ảnh
        public string subjectCode;  // ví dụ: COOL325364
    }

    private string FormatClassLabel(ClassOption opt)
    {
        // Ưu tiên tên môn, nếu không có thì dùng mã học phần
        string name = !string.IsNullOrEmpty(opt.subjectName) ? opt.subjectName : opt.subjectCode;
        if (string.IsNullOrEmpty(name)) name = "Môn chưa rõ";

        // Format: Tên môn (mã học phần ưu tiên, fallback mã lớp)
        var code = !string.IsNullOrEmpty(opt.subjectCode) ? opt.subjectCode : opt.classCode;
        return $"{name} ({code})";
    }

    private void SetupClassSwitcher()
    {
        // Home: card switcher
        _cardClassSwitcher = _uiRoot.Q<VisualElement>("CardClassSwitcher");
        _lblSwitcherSub = _cardClassSwitcher?.Q<Label>(className: "stat-sub");
        if (_cardClassSwitcher != null)
        {
            _cardClassSwitcher.RegisterCallback<ClickEvent>(_ => ShowClassPicker());
        }

        // Stats tab: class bar
        _btnStatsChangeClass = _uiRoot.Q<Button>("BtnStatsChangeClass");
        _lblStatsSelectedClass = _uiRoot.Q<Label>("LblStatsSelectedClass");
        if (_btnStatsChangeClass != null)
        {
            _btnStatsChangeClass.clicked -= ShowClassPicker;
            _btnStatsChangeClass.clicked += ShowClassPicker;
        }

        // Home: label in Total Students card
        _lblCurrentClass = _uiRoot.Q<Label>("LblCurrentClass");

        EnsureDefaultClassSelected();
        UpdateClassLabels();
    }

    private void EnsureDefaultClassSelected()
    {
        var options = GetClassOptionsFromSchedule();
        if (options.Count == 0)
        {
            _currentClassCode = "";
            return;
        }

        // nếu chưa chọn hoặc class cũ không tồn tại nữa
        if (string.IsNullOrEmpty(_currentClassCode) || !options.Any(o => o.classCode == _currentClassCode))
        {
            _currentClassCode = options[0].classCode;
            SaveScheduleToLocal();
        }
    }

    private void UpdateClassLabels()
    {
        string display = "";
        if (!string.IsNullOrEmpty(_currentClassCode))
        {
            var opt = GetClassOptionsFromSchedule().FirstOrDefault(o => o.classCode == _currentClassCode);
            display = (!string.IsNullOrEmpty(opt.classCode)) ? FormatClassLabel(opt) : _currentClassCode;
        }

        if (_lblSwitcherSub != null)
            _lblSwitcherSub.text = string.IsNullOrEmpty(_currentClassCode) ? "Nhấn để chọn lớp" : $"Đang chọn: {display}";

        if (_lblStatsSelectedClass != null)
            _lblStatsSelectedClass.text = string.IsNullOrEmpty(_currentClassCode) ? "Chưa chọn lớp" : display;

        if (_lblCurrentClass != null)
            _lblCurrentClass.text = string.IsNullOrEmpty(_currentClassCode) ? "Lớp: --" : $"Lớp: {display}";
    }

    private List<ClassOption> GetClassOptionsFromSchedule()
    {
        if (_scheduleList == null) return new List<ClassOption>();

        return _scheduleList
            .Where(s => !string.IsNullOrEmpty(s.classCode))
            .GroupBy(s => s.classCode.Trim())
            .Select(g =>
            {
                var one = g.First();
                return new ClassOption
                {
                    classCode = g.Key,
                    subjectName = one.subjectName ?? "",
                    subjectCode = one.subjectCode ?? ""
                };
            })
            .OrderBy(x => string.IsNullOrEmpty(x.subjectName) ? x.subjectCode : x.subjectName)
            .ThenBy(x => x.classCode)
            .ToList();
    }

    private void BindClassPickerModal()
    {
        _classPickerOverlay = _uiRoot.Q<VisualElement>("ClassPickerOverlay");
        _classPickerCard = _uiRoot.Q<VisualElement>("ClassPickerCard");
        _classPickerList = _uiRoot.Q<ScrollView>("ClassPickerList");
        _btnClassPickerClose = _uiRoot.Q<Button>("BtnClassPickerClose");

        if (_classPickerOverlay == null || _classPickerList == null)
        {
            Debug.LogWarning("[ClassPicker] Missing UXML elements. Check names in MainDashboard.uxml");
            return;
        }

        if (_btnClassPickerClose != null)
            _btnClassPickerClose.clicked += HideClassPicker;

        // Click outside card to close
        _classPickerOverlay.RegisterCallback<ClickEvent>(evt =>
        {
            if (evt.target == _classPickerOverlay) HideClassPicker();
        });
    }

    private void ShowClassPickerModal()
    {
        if (_classPickerOverlay == null) return;
        _classPickerOverlay.RemoveFromClassList("is-hidden");
    }

    private void HideClassPicker()
    {
        if (_classPickerOverlay == null) return;
        _classPickerOverlay.AddToClassList("is-hidden");
    }

    private void ShowClassPicker()
    {
        var options = GetClassOptionsFromSchedule();
        if (options.Count == 0)
        {
            ShowToast("📌 Chưa có lịch dạy. Hãy import lịch trước!", colOrange);
            return;
        }

        if (_classPickerList == null)
        {
            Debug.LogWarning("[ClassPicker] Modal not bound. Call BindClassPickerModal() in OnEnable.");
            return;
        }

        _classPickerList.Clear();

        foreach (var opt in options)
        {
            var cc = opt.classCode; // capture for lambda
            var btn = new Button(() =>
            {
                SwitchClass(cc);
                HideClassPicker();
            });

            btn.text = FormatClassLabel(opt);
            btn.AddToClassList("classpicker-item");

            if (opt.classCode == _currentClassCode)
                btn.AddToClassList("is-selected");

            _classPickerList.Add(btn);
        }

        ShowClassPickerModal();
    }

    private void SwitchClass(string newClassCode)
    {
        if (newClassCode == _currentClassCode) return;

        Debug.Log($"[ClassSwitch] Switching from '{_currentClassCode}' to '{newClassCode}'");

        // Save logs lớp cũ
        SaveLogsLocal();

        _currentClassCode = newClassCode;
        _currentTermId = CurrentTermId;

        // bump version để request cũ không được apply
        _tenantContextVersion++;

        SaveScheduleToLocal();

        // clear caches để UI không “dính”
        _allLogs = new List<AttendanceLog>();
        _hiddenLogIds = new HashSet<string>();
        _allProfilesCache = new List<FaceProfile>();

        if (_studentDataList == null) _studentDataList = new List<StudentStat>();
        _studentDataList.Clear();

        // load local theo lớp mới
        LoadLogsLocal();
        LoadHiddenLogIds();
        LoadStudentDataListFromFile();

        // update UI ngay (không đợi server)
        UpdateClassLabels();
        RefreshMainContent();
        UpdateStatsFromLogs();
        UpdateTrendChart();
        UpdateStatsTab();
        FilterStudentList(_inpSearchStudent?.value ?? "");

        // fetch profiles (để status/avatar đúng) + render tab register
        int ctx = _tenantContextVersion;
        StartCoroutine(GetProfiles((profiles) =>
        {
            if (ctx != _tenantContextVersion) return;
            _allProfilesCache = profiles?.ToList() ?? new List<FaceProfile>();
            _skipNextProfilesFetchInRegisterTab = true;

            RenderRegisterTabContent();
            FilterStudentList(_inpSearchStudent?.value ?? "");
            UpdateStatsFromLogs();
            UpdateTrendChart();
            UpdateStatsTab();
        }));

        // fetch logs server lớp mới
        StartCoroutine(FetchLogsFromServer(silent: true));

        ShowToast($"✅ Đã chuyển sang lớp: {newClassCode}", colGreen);
    }

    #region Sidebar Collapse
    private void SetupSidebarCollapse()
    {
        _uiSidebar = _uiRoot.Q<VisualElement>("Sidebar");
        _btnSidebarToggle = _uiRoot.Q<Button>("BtnSidebarToggle");
        if (_uiSidebar == null || _btnSidebarToggle == null) return;

        _isSidebarCollapsed = PlayerPrefs.GetInt("SidebarCollapsed", 0) == 1;
        ApplySidebarCollapse();

        _btnSidebarToggle.clicked += ToggleSidebarCollapse;
    }

    private void ToggleSidebarCollapse()
    {
        _isSidebarCollapsed = !_isSidebarCollapsed;
        PlayerPrefs.SetInt("SidebarCollapsed", _isSidebarCollapsed ? 1 : 0);
        ApplySidebarCollapse();
    }

    private void ApplySidebarCollapse()
    {
        if (_uiSidebar == null) return;
        _uiSidebar.EnableInClassList("is-collapsed", _isSidebarCollapsed);
        if (_btnSidebarToggle != null)
            _btnSidebarToggle.text = _isSidebarCollapsed ? "⟫" : "⟪";

        // Tooltip khi collapsed
        _uiRoot.Query<Button>(className: "menu-item").ForEach(btn =>
        {
            var textLabel = btn.Q<Label>(className: "menu-text");
            btn.tooltip = _isSidebarCollapsed ? (textLabel?.text ?? "") : "";
        });
    }
    #endregion

    #region Google Sheets Sync
    
    // [NEW] Google Sheets sync - Model classes (sessionKey-based for idempotency)
    [Serializable]
    public class GSheetWeekUpdateRequest
    {
        public string sessionKey;    // unique per buổi để tránh cộng dồn 2 lần
        public string subjectCode;   // mã học phần
        public string subjectName;   // tên môn
        public string sessionDate;   // yyyy-MM-dd
        public List<GSheetStudentRow> rows;
    }

    [Serializable]
    public class GSheetStudentRow
    {
        public string mssv;
        public string name;
        public string status; // "P" | "L" | "A"
    }

    // [NEW] Auto-sync to Google Sheets when session ends (call from Update)
    private void AutoSyncGoogleSheetIfNeeded()
    {
        // throttle: 10s check 1 lần
        if (DateTime.Now < _nextGSheetAutoCheckAt) return;
        _nextGSheetAutoCheckAt = DateTime.Now.AddSeconds(10);

        if (_uiRoot == null) return;

        // Chỉ chạy khi đang ở tab Attendance
        var att = _uiRoot.Q<VisualElement>("AttendanceContainer");
        bool attVisible = att != null && att.resolvedStyle.display != DisplayStyle.None;
        if (!attVisible) return;

        // Cần roster + schedule
        int totalStudents = _studentDataList?.Count ?? 0;
        if (totalStudents <= 0) return;
        if (_scheduleList == null || _scheduleList.Count == 0) return;

        DateTime now = DateTime.Now;
        DateTime today = now.Date;

        var occOpt = FindHomeSessionOccurrence(today, now);
        if (!occOpt.HasValue) return;

        var occ = occOpt.Value;

        // chỉ sync sau khi buổi kết thúc (Option A)
        if (now < occ.End) return;

        // cần có mã học phần
        string subjectCode = occ.Session.subjectCode ?? "";
        string subjectName = occ.Session.subjectName ?? "";
        if (string.IsNullOrEmpty(subjectCode)) return;

        // sessionKey unique (tránh cộng dồn 2 lần)
        string sessionKey = $"{today:yyyyMMdd}_{occ.Session.dayOfWeek}_{occ.Session.startPeriod}_{occ.Session.endPeriod}_{subjectCode}";
        if (_gsheetSyncedSessionKeys.Contains(sessionKey)) return;

        // lấy check-in sớm nhất mỗi SV trong khung buổi
        var earliest = GetEarliestCheckinByStudent(occ);

        // GUARD: tránh chốt nhầm khi buổi không thực sự điểm danh
        int minLogs = Mathf.Clamp(totalStudents / 25, MIN_LOGS_AUTO_SYNC_MIN, MIN_LOGS_AUTO_SYNC_MAX);
        if (earliest.Count < minLogs) return;

        // Build rows P/L/A
        var rows = new List<GSheetStudentRow>(totalStudents);
        DateTime lateAfter = occ.Start.AddMinutes(LATE_GRACE_MINUTES);

        foreach (var st in _studentDataList)
        {
            string m = st?.MSSV?.Trim();
            if (string.IsNullOrEmpty(m)) continue;

            string status = "A"; // default vắng (vì đang chốt cuối buổi)
            if (earliest.TryGetValue(m, out DateTime t))
            {
                status = (t > lateAfter) ? "L" : "P";
            }

            rows.Add(new GSheetStudentRow
            {
                mssv = m,
                name = st.Name ?? "",
                status = status
            });
        }

        var payload = new GSheetWeekUpdateRequest
        {
            sessionKey = sessionKey,
            subjectCode = subjectCode,
            subjectName = subjectName,
            sessionDate = today.ToString("yyyy-MM-dd"),
            rows = rows
        };

        StartCoroutine(PostGSheetWeekUpdate(payload, sessionKey));
    }

    // [NEW] Coroutine to post GSheet update
    private IEnumerator PostGSheetWeekUpdate(GSheetWeekUpdateRequest payload, string sessionKey)
    {
        string url = cloudRunApiUrl.TrimEnd('/') + "/gsheets/update_week";
        string json = JsonUtility.ToJson(payload);
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);

        Debug.Log($"[GSheets] Auto-syncing session {sessionKey}...");

        using (var req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            ApplyTenantHeaders(req);

            req.timeout = 120;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("[GSheets] Sync failed: " + req.error + " | " + req.downloadHandler?.text);
                yield break;
            }

            _gsheetSyncedSessionKeys.Add(sessionKey);
            Debug.Log("[GSheets] Sync OK: " + req.downloadHandler?.text);
            ShowToast("✅ Đã cập nhật Google Sheets!", colGreen);
        }
    }

    // ================= GOOGLE SHEETS BOOTSTRAP (CREATE TABS ON SCHEDULE IMPORT) =================

    [Serializable]
    public class GSheetSubject
    {
        public string subjectCode;
        public string subjectName;
    }

    [Serializable]
    public class GSheetBootstrapRequest
    {
        public List<GSheetSubject> subjects;
        public string phase1Start;
        public string phase1End;
        public string phase2Start;
        public string phase2End;
    }

    [Serializable]
    public class GSheetBootstrapResponse
    {
        public string status;
        public string spreadsheet_id;
        public string spreadsheet_url;
        public int tabs_created_or_ensured;
        public bool has_phase2;
    }

    // [NEW] Call this after schedule import to create sheet tabs per subject
    private IEnumerator BootstrapGoogleSheetsAfterScheduleImport()
    {
        if (!_isLoggedIn || string.IsNullOrEmpty(_currentTeacherId)) yield break;
        if (_scheduleList == null || _scheduleList.Count == 0) yield break;

        // Gather unique subjects by subjectCode
        var map = new Dictionary<string, GSheetSubject>();
        foreach (var s in _scheduleList)
        {
            string code = (s.subjectCode ?? "").Trim();
            string name = (s.subjectName ?? "").Trim();
            if (string.IsNullOrEmpty(code)) continue;
            if (!map.ContainsKey(code))
                map[code] = new GSheetSubject { subjectCode = code, subjectName = name };
        }
        if (map.Count == 0) yield break;

        var payload = new GSheetBootstrapRequest
        {
            subjects = map.Values.ToList(),
            phase1Start = _playerPhase1Start,
            phase1End = _playerPhase1End,
            phase2Start = _playerPhase2Start,
            phase2End = _playerPhase2End
        };

        ShowToast("⏳ Đang tạo Google Sheets theo môn...", colBlue);

        string url = cloudRunApiUrl.TrimEnd('/') + "/gsheets/bootstrap";
        string json = JsonUtility.ToJson(payload);

        using (var req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            ApplyTenantHeaders(req);
            req.timeout = 120;

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("[GSheets] Bootstrap failed: " + req.error + " | " + req.downloadHandler?.text);
                ShowToast("⚠️ Tạo Google Sheets thất bại", colOrange);
                yield break;
            }

            // Parse response and copy spreadsheet URL
            var txt = req.downloadHandler.text ?? "";
            var resp = JsonUtility.FromJson<GSheetBootstrapResponse>(txt);

            if (resp != null && !string.IsNullOrEmpty(resp.spreadsheet_url))
            {
                SaveGSheetUrlForTenant(resp.spreadsheet_url);
                GUIUtility.systemCopyBuffer = resp.spreadsheet_url;
                ShowToast("✅ Đã tạo Google Sheets (đã copy link)", colGreen);
                Debug.Log("[GSheets] Bootstrap OK: " + resp.spreadsheet_url);
            }
            else
            {
                // fallback: nếu parse fail thì vẫn copy raw để debug
                GUIUtility.systemCopyBuffer = txt;
                ShowToast("✅ Đã tạo Google Sheets (copy phản hồi)", colGreen);
                Debug.Log("[GSheets] Bootstrap OK raw: " + txt);
            }
        }
    }

    // ================= GOOGLE SHEETS ROSTER SYNC =================

    [Serializable]
    public class GSheetRosterSyncResponse
    {
        public bool ok;
        public string spreadsheet_url;
        public int tabs_processed;
        public int rows_synced;
    }

    [Serializable]
    public class GSheetRosterSyncRequest
    {
        public string phase1Start;
        public string phase1End;
        public string phase2Start;
        public string phase2End;
    }

    // [NEW] Sync roster to all Google Sheet tabs (call after importing student list)
    private IEnumerator SyncRosterToGoogleSheets()
    {
        if (!_isLoggedIn || string.IsNullOrEmpty(_currentTeacherId)) yield break;

        string url = cloudRunApiUrl.TrimEnd('/') + "/gsheets/sync_roster";

        var payload = new GSheetRosterSyncRequest
        {
            phase1Start = _playerPhase1Start,
            phase1End = _playerPhase1End,
            phase2Start = _playerPhase2Start,
            phase2End = _playerPhase2End
        };
        string json = JsonUtility.ToJson(payload);

        using (var req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            ApplyTenantHeaders(req);
            req.timeout = 120;

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("[GSheets] SyncRoster failed: " + req.error + " | " + req.downloadHandler?.text);
                yield break;
            }

            var txt = req.downloadHandler.text ?? "";
            var resp = JsonUtility.FromJson<GSheetRosterSyncResponse>(txt);

            if (resp != null && !string.IsNullOrEmpty(resp.spreadsheet_url))
            {
                SaveGSheetUrlForTenant(resp.spreadsheet_url);
                GUIUtility.systemCopyBuffer = resp.spreadsheet_url;
            }

            Debug.Log("[GSheets] SyncRoster OK: " + txt);
            ShowToast("✅ Đã đồng bộ danh sách lên Google Sheets", colGreen);
        }
    }

    // [NEW] Helper: Extract subjectCode from subject string (various formats)
    private (string subjectName, string subjectCode) ExtractSubjectCodeFromString(string raw)
    {
        raw = (raw ?? "").Trim();
        string subjName = raw;
        string subjCode = "";

        // Case 1: "Tên môn (COOL325364)"
        int lp = raw.LastIndexOf('(');
        int rp = raw.LastIndexOf(')');
        if (lp >= 0 && rp > lp + 1 && rp == raw.Length - 1)
        {
            var inside = raw.Substring(lp + 1, rp - lp - 1).Trim();
            if (inside.Length >= 3 && inside.Length <= 32 && inside.IndexOf(' ') < 0)
            {
                subjCode = inside;
                subjName = raw.Substring(0, lp).Trim();
            }
        }

        // Case 2: "COOL325364 - Tên môn"
        if (string.IsNullOrEmpty(subjCode))
        {
            var dash = raw.Split(new char[] { '-', '–' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (dash.Length == 2)
            {
                var left = dash[0].Trim();
                var right = dash[1].Trim();
                bool looksLikeCode = left.Length >= 4 && left.Length <= 32 && left.IndexOf(' ') < 0 && left.Any(char.IsDigit);
                if (looksLikeCode)
                {
                    subjCode = left;
                    subjName = right;
                }
            }
        }

        // Case 3: "COOL325364 Tên môn"
        if (string.IsNullOrEmpty(subjCode))
        {
            var parts = raw.Split(new char[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                var first = parts[0].Trim();
                bool looksLikeCode = first.Length >= 4 && first.Length <= 32 && first.IndexOf(' ') < 0 && first.Any(char.IsDigit);
                if (looksLikeCode)
                {
                    subjCode = first;
                    subjName = parts[1].Trim();
                }
            }
        }

        return (subjName, subjCode);
    }

    // [REMOVED DUPLICATE GOOGLE SHEETS/OAUTH LOGIC - REPLACED BY NEW IMPLEMENTATION AT END OF FILE]

    // ================= "CHỐT BUỔI" - FINALIZE SESSION TO GOOGLE SHEET =================

    [Serializable]
    public class GSheetRowUpdate
    {
        public string mssv;
        public string name;
        public string value; // P, L, A
    }

    [Serializable]
    public class GSheetWeekUpdateReq
    {
        public string sessionKey;
        public string subjectCode;
        public string subjectName;
        public string sessionDate;
        public List<GSheetRowUpdate> rows;
    }

    // (Helper functions preserved)
    private int GetWeekIndexForDate(DateTime date)
    {
        if (DateTime.TryParseExact(_playerPhase1Start, "dd/MM/yyyy", null,
            System.Globalization.DateTimeStyles.None, out var start))
        {
            int w = (int)((date.Date - start.Date).TotalDays / 7) + 1;
            if (w < 1) w = 1;
            return w;
        }
        return 1;
    }

    private string GetSheetTitleForCurrentSession()
    {
        return !string.IsNullOrEmpty(_currentSubjectName) ? $"{_currentSubjectName} ({_currentSubjectCode})" : "Điểm danh";
    }

    private Dictionary<string, string> BuildStatusByMssvForToday()
    {
        var map = new Dictionary<string, string>();
        string dStr = DateTime.Today.ToString("dd/MM/yyyy");

        foreach (var st in _studentDataList)
        {
            var log = _allLogs?.FirstOrDefault(l => l.mssv == st.MSSV && l.date == dStr);
            if (log == null) { map[st.MSSV] = "A"; continue; }
            string computed = ComputeStatusByTime(log.time, log.status);
            map[st.MSSV] = (computed == "Late") ? "L" : "P";
        }
        return map;
    }

    private IEnumerator FinalizeAndSyncToGoogleSheet()
    {
        // 1. Prepare Data
        // Tạo sessionKey unique cho ngày hôm nay + môn học (để backend check idempotent)
        string dateIso = DateTime.Today.ToString("yyyy-MM-dd");
        string subjectCode = string.IsNullOrEmpty(_currentSubjectCode) ? "MISC" : _currentSubjectCode;
        string subjectName = string.IsNullOrEmpty(_currentSubjectName) ? "Hoạt động khác" : _currentSubjectName;
        string sessionKey = $"{subjectCode}_{dateIso}"; 
        // Note: Nếu 1 ngày học 2 ca môn giống nhau thì cần thêm ca vào key. Tạm thời dùng ngày.

        var statusMap = BuildStatusByMssvForToday();
        var rows = new List<GSheetRowUpdate>();
        foreach (var kv in statusMap)
        {
            string mssv = kv.Key;
            var st = _studentDataList.FirstOrDefault(s => s.MSSV == mssv);
            string name = st != null ? st.Name : "";
            rows.Add(new GSheetRowUpdate { mssv = mssv, name = name, value = kv.Value });
        }

        // 2. Setup Request
        string url = cloudRunApiUrl.TrimEnd('/') + "/gsheets/update_week";
        var body = new GSheetWeekUpdateReq
        {
            sessionKey = sessionKey,
            subjectCode = subjectCode,
            subjectName = subjectName,
            sessionDate = dateIso,
            rows = rows
        };
        string json = JsonUtility.ToJson(body);

        Debug.Log($"[GSheets] Sending update_week: {json}");

        using (var req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            ApplyTenantHeaders(req);
            req.timeout = 30;

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[GSheets] Network Error: {req.error}");
                ShowToast("❌ Lỗi mạng khi chốt buổi", colRed);
                yield break;
            }

            // 3. Handle Response (User Logic)
            var resText = req.downloadHandler.text;
            Debug.Log("[GSheets] update_week raw: " + resText);

            GSheetWeekUpdateResponse res = null;
            try
            {
                res = JsonUtility.FromJson<GSheetWeekUpdateResponse>(resText);
            }
            catch (System.Exception e)
            {
                Debug.LogError("[GSheets] update_week parse failed: " + e.Message);
            }

            bool ok = (res != null) && (res.ok || res.already_synced || res.status == "success");
            if (!ok)
            {
                var st = res != null ? res.status : "";
                var msg = res != null ? res.message : "Update week failed";
                var err = res != null ? res.error : "";

                Debug.LogError($"[GSheets] update_week failed status={st} msg={msg} err={err}");
                ShowToast($"❌ Lỗi: {msg}", colRed);

                // Nếu cần google auth -> bật flow connect
                if (res != null && st == "need_google_auth")
                {
                    Debug.Log("[GSheets] Need Google auth -> start oauth connect flow");
                    ShowToast("⚠️ Cần kết kết nối Google...", colOrange);
                    // Dùng StartGoogleConnectFlow (flow connect chuẩn)
                    yield return StartCoroutine(StartGoogleOAuth(() => {
                        StartCoroutine(FinalizeAndSyncToGoogleSheet()); // Retry after success
                    }));
                }

                yield break; 
            }

            // Thành công hoặc đã synced
            Debug.Log($"[GSheets] update_week ok. already_synced={res.already_synced} phase={res.phase} week={res.week} col={res.col}");
            
            if (res.already_synced)
                ShowToast("✅ Buổi này đã chốt trước đó!", colGreen);
            else
                ShowToast($"✅ Đã cập nhật tuần {res.week} (Cột {res.col})", colGreen);
            
            if (!string.IsNullOrEmpty(res.spreadsheet_url))
            {
                // [NEW] Save URL so the button appears
                SaveGSheetUrlForTenant(res.spreadsheet_url);
                UpdateOpenGSheetButton();

                Application.OpenURL(res.spreadsheet_url);
            }
        }
    }

    private IEnumerator CheckGoogleConnected(System.Action<bool> callback)
    {
        string url = cloudRunApiUrl.TrimEnd('/') + "/google/oauth/status";
        using (var req = UnityWebRequest.Get(url))
        {
            ApplyTenantHeaders(req);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout = 10;
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                var resp = JsonUtility.FromJson<OAuthStatusResp>(req.downloadHandler.text);
                callback(resp != null && resp.connected && resp.has_refresh_token);
            }
            else
            {
                callback(false);
            }
        }
    }

    private IEnumerator OpenGoogleSheetDirect()
    {
        string url = cloudRunApiUrl.TrimEnd('/') + "/gsheets/open";
        using (var req = UnityWebRequest.Get(url))
        {
            ApplyTenantHeaders(req);
            req.timeout = 15;
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                var resp = JsonUtility.FromJson<OpenSheetResp>(req.downloadHandler.text);
                if (resp != null && resp.status == "success" && !string.IsNullOrEmpty(resp.spreadsheet_url))
                {
                    Application.OpenURL(resp.spreadsheet_url);
                }
            }
        }
    }

    #endregion

    // =================================================================================
    // NEW GOOGLE OAUTH & SHEETS LOGIC (APPENDED)
    // =================================================================================

    // 1. SETUP CONNECT BUTTON
    private void SetupConnectGoogleButton()
    {
        void Bind(Button b, string tag)
        {
            if (b == null)
            {
                Debug.LogWarning($"[UI] BtnConnectGoogle not found in {tag}");
                return;
            }

            b.clicked -= OnConnectGoogleClicked;
            b.clicked += OnConnectGoogleClicked;

            Debug.Log($"[UI] Bound BtnConnectGoogle ({tag})");
        }

        var btnStudent  = _uiRoot?.Q<VisualElement>("StudentListContainer")?.Q<Button>("BtnConnectGoogle");
        var btnSettings = _uiRoot?.Q<VisualElement>("SettingsContainer")?.Q<Button>("BtnConnectGoogle");

        Bind(btnStudent, "StudentList");
        if (btnSettings != btnStudent) Bind(btnSettings, "Settings");
    }

    void OnConnectGoogleClicked()
    {
        Debug.Log("[UI] Connect Google clicked");
        ShowToast("🌐 Đang mở Google OAuth...", colAccent);
        // Start Poll Flow
        StartCoroutine(StartGoogleOAuth(() => {
            StartCoroutine(EnsureGoogleSheetAndOpen());
        }));
    }

    // 2. SETUP OPEN SHEET BUTTON (Settings)
    private Button _btnOpenGSheet;

    void SetupOpenGSheetButton()
    {
        _btnOpenGSheet = _uiRoot?.Q<Button>("BtnOpenGSheet");
        if (_btnOpenGSheet != null)
        {
            _btnOpenGSheet.clicked -= OnOpenGSheetClicked;
            _btnOpenGSheet.clicked += OnOpenGSheetClicked;
        }
        UpdateOpenGSheetButton();
    }

    // 3. SETUP OPEN SHEET BUTTON (Student List)
    private Button _btnOpenGoogleSheetStudentList;

    void SetupOpenGoogleSheetStudentListButton()
    {
        var container = _uiRoot?.Q<VisualElement>("StudentListContainer");
        _btnOpenGoogleSheetStudentList = container?.Q<Button>("BtnOpenGoogleSheetStudentList");

        if (_btnOpenGoogleSheetStudentList != null)
        {
            _btnOpenGoogleSheetStudentList.clicked -= OnOpenGSheetClicked;
            _btnOpenGoogleSheetStudentList.clicked += OnOpenGSheetClicked;
        }
        UpdateOpenGSheetButton();
    }

    // Common handler
    void OnOpenGSheetClicked()
    {
        // Gọi ensure v2 để chắc chắn sheet còn sống và mở
        StartCoroutine(EnsureGoogleSheetAndOpen());
    }

    void UpdateOpenGSheetButton()
    {
        string savedUrl = LoadGSheetUrlForTenant();
        var display = !string.IsNullOrEmpty(savedUrl) ? DisplayStyle.Flex : DisplayStyle.None;

        if (_btnOpenGoogleSheetStudentList != null) _btnOpenGoogleSheetStudentList.style.display = display;
        if (_btnOpenGSheet != null)                 _btnOpenGSheet.style.display = display;
    }



    // 4. Persistence
    void SaveGSheetUrlForTenant(string url)
    {
        if (string.IsNullOrEmpty(_currentTeacherId)) return; // safety
        string key = $"GSheetUrl_{_currentTeacherId}";
        PlayerPrefs.SetString(key, url);
        PlayerPrefs.Save();
        Debug.Log($"[GSheet] Saved URL for {_currentTeacherId}: {url}");
    }

    string LoadGSheetUrlForTenant()
    {
        if (string.IsNullOrEmpty(_currentTeacherId)) return null;
        string key = $"GSheetUrl_{_currentTeacherId}";
        return PlayerPrefs.GetString(key, null);
    }

    // 5. Ensure V2 Coroutine (Thay thế EnsureGoogleSheetAndOpen cũ nếu có, hoặc tạo mới)
    // [FIX] Use ensure_v2
    IEnumerator EnsureGoogleSheetAndOpen()
    {
        ShowToast("⏳ Đang kiểm tra Google Sheet...", colAccent);

        string url = cloudRunApiUrl.TrimEnd('/') + "/gsheets/ensure_v2"; // endpoint v2
        // Dùng GET cho ensure_v2
        using (var req = UnityWebRequest.Get(url))
        {
             ApplyTenantHeaders(req);
             req.timeout = 20;

             yield return req.SendWebRequest();

             if (req.result != UnityWebRequest.Result.Success)
             {
                 Debug.LogError($"[GSheet] Ensure error: {req.error}");
                 ShowToast($"❌ Lỗi kết nối: {req.error}", colRed);
                 yield break;
             }

             var text = req.downloadHandler.text;
             Debug.Log($"[GSheet] ensure_v2 res: {text}");

             GSheetEnsureResV2 res = null;
             try { res = JsonUtility.FromJson<GSheetEnsureResV2>(text); } catch {}

             if (res != null)
             {
                 if (res.status == "need_google_auth")
                 {
                     ShowToast("⚠️ Cần cấp quyền Google trước!", colOrange);
                     StartCoroutine(StartGoogleOAuth(() => {
                         StartCoroutine(EnsureGoogleSheetAndOpen());
                     }));
                 }
                 else if (res.ok || res.status == "success" || res.status == "created" || res.status == "existed")
                 {
                     string targetUrl = !string.IsNullOrEmpty(res.url) ? res.url : res.spreadsheet_url;
                     
                     if (!string.IsNullOrEmpty(targetUrl))
                     {
                         // Save for UI
                         SaveGSheetUrlForTenant(targetUrl);
                         UpdateOpenGSheetButton();

                         // Bootstrap: đẩy danh sách sinh viên + tạo tab môn học, rồi mở sheet
                         StartCoroutine(BootstrapGoogleSheet(targetUrl));
                     }
                     else
                     {
                         ShowToast("⚠️ Không tìm thấy URL sheet.", colOrange);
                     }
                 }
                 else
                 {
                     ShowToast($"❌ Lỗi: {res.message}", colRed);
                 }
             }
        }
    }

    // 5b. Bootstrap: đẩy roster + tạo tab môn học vào Sheet
    IEnumerator BootstrapGoogleSheet(string sheetUrl)
    {
        ShowToast("⏳ Đang đồng bộ danh sách lớp...", colAccent);

        string url = cloudRunApiUrl.TrimEnd('/') + "/gsheets/bootstrap";
        string json = "{}"; // server tự lấy roster + schedule từ Firestore

        using (var req = new UnityWebRequest(url, "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            ApplyTenantHeaders(req);
            req.timeout = 30;

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[GSheet] Bootstrap error: {req.error}");
                // Vẫn mở sheet dù bootstrap thất bại
            }
            else
            {
                Debug.Log($"[GSheet] Bootstrap res: {req.downloadHandler.text}");
            }
        }

        ShowToast("✅ Đã mở Google Sheet!", colGreen);
        Application.OpenURL(sheetUrl);
    }

    // 6. Connect Flow (Start & Poll)
    // [NEW] Helper classes for OAuth
    [System.Serializable]
    public class OAuthStartResp
    {
        public string authorization_url;
        public string poll_token;
    }

    [System.Serializable]
    public class OAuthPollResp
    {
        public string status; // pending, done, error
        public string teacher_id;
        public string email;
        public bool has_refresh_token; 
        public string error;
    }


    private void EnsureToastOverlay()
    {
        if (_toastContainer != null || _uiRoot == null) return;

        // Check if already exists in query to be safe
        var existing = _uiRoot.Q<VisualElement>("ToastContainer");
        if (existing != null)
        {
            _toastContainer = existing;
            return;
        }

        _toastContainer = new VisualElement { name = "ToastContainer" };
        _toastContainer.style.position = Position.Absolute;
        _toastContainer.style.top = 20;
        _toastContainer.style.left = 0;
        _toastContainer.style.right = 0;
        _toastContainer.style.alignItems = Align.Center;
        // _toastContainer.style.zIndex = 9999; // [FIX] zIndex not supported in IStyle, rely on hierarchy order or BringToFront()
        
        // Pickign mode ignore to let clicks pass through empty areas? 
        // Better: let container be pickable but items? 
        _toastContainer.pickingMode = PickingMode.Ignore;

        _uiRoot.Add(_toastContainer);
    }

    // 6. Connect Flow (Start & Poll)
    IEnumerator StartGoogleOAuth(System.Action onSuccess = null)
    {
        EnsureToastOverlay();

        if (string.IsNullOrWhiteSpace(_currentTeacherId))
        {
            Debug.LogWarning("[OAuth] Missing _currentTeacherId -> need login first");
            ShowToast("⚠️ Bạn cần đăng nhập trước khi kết nối Google", colOrange);
            yield break;
        }

        string baseUrl = (cloudRunApiUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
        {
            Debug.LogWarning("[OAuth] cloudRunApiUrl is empty");
            ShowToast("⚠️ Thiếu cấu hình API URL", colOrange);
            yield break;
        }

        // [FIX] Correct endpoint from main.py
        string url = baseUrl + "/google/oauth/start";
        Debug.Log($"[OAuth] Start GET {url} teacher={_currentTeacherId} term={_currentTermId} class={_currentClassCode}");

        using (var req = UnityWebRequest.Get(url))
        {
            ApplyTenantHeaders(req);
            req.timeout = 30;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                var body = req.downloadHandler?.text;
                Debug.LogWarning($"[OAuth] Start failed code={req.responseCode} err={req.error} body={body}");
                ShowToast("⚠️ Lỗi xin link kết nối Google", colRed);
                yield break;
            }

            var resp = JsonUtility.FromJson<OAuthStartResp>(req.downloadHandler.text);
            if (resp == null || string.IsNullOrEmpty(resp.authorization_url))
            {
                Debug.LogWarning("[OAuth] No authorization_url: " + req.downloadHandler.text);
                ShowToast("⚠️ OAuth URL response lỗi", colOrange);
                yield break;
            }

            GUIUtility.systemCopyBuffer = resp.authorization_url;
            Debug.Log("[OAuth] OpenURL: " + resp.authorization_url);

            Application.OpenURL(resp.authorization_url);
            ShowToast("🌐 Đã mở trình duyệt (đã copy link).", colBlue);

            if (!string.IsNullOrEmpty(resp.poll_token))
            {
                yield return StartCoroutine(PollGoogleOAuthStatus(resp.poll_token, onSuccess));
            }
        }
    }

    // 7. Poll Login
    IEnumerator PollGoogleOAuthStatus(string pollToken, System.Action onSuccess = null)
    {
        EnsureToastOverlay();
        // Poll for 2 minutes
        System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
        sw.Start();

        string baseUrl = (cloudRunApiUrl ?? "").Trim().TrimEnd('/');

        while (sw.Elapsed.TotalSeconds < 160)
        {
            yield return new WaitForSeconds(3f);

            // [FIX] Correct poll endpoint from main.py is /auth/google/poll OR /google/oauth/poll?
            // main.py has: @app.route("/auth/google/poll", methods=["GET"]) -> def auth_google_poll()
            // And logic checks poll_token.
            string url = $"{baseUrl}/auth/google/poll?p={pollToken}";
            
            using (var req = UnityWebRequest.Get(url))
            {
                // Don't need headers for poll usually but no harm
                // ApplyTenantHeaders(req); 
                yield return req.SendWebRequest();
                
                if (req.result == UnityWebRequest.Result.Success)
                {
                    var txt = req.downloadHandler.text; 
                    // Expected: {"status": "done", "email": "...", "has_refresh_token": true ...}
                    var p = JsonUtility.FromJson<OAuthPollResp>(txt);

                    if (p != null)
                    {
                        if (p.status == "done")
                        {
                            ShowToast($"✅ Đã kết nối: {p.email}", colGreen);
                            // Refresh buttons
                            UpdateOpenGSheetButton();
                            onSuccess?.Invoke();
                            yield break;
                        }
                        else if (p.status == "error")
                        {
                            ShowToast($"❌ Lỗi kết nối: {p.error}", colRed);
                            yield break;
                        }
                    }
                }
            }
        }
        ShowToast("⌛ Hết thời gian chờ kết nối Google", colOrange);
    }
    // =================================================================================
    // GOOGLE LOGIN FLOW (Global Login)
    // =================================================================================
    
    private Button _btnLoginGoogle;

    private void SetupGoogleLoginButton()
    {
        _btnLoginGoogle = _uiRoot?.Q<Button>("BtnLoginGoogle");
        if (_btnLoginGoogle == null)
        {
            // Debug.LogWarning("[GoogleLogin] BtnLoginGoogle not found in UXML.");
            // Try searching inside LoginOverlay specifically if global fail (rare)
            var overlay = _uiRoot?.Q<VisualElement>("LoginOverlay");
            _btnLoginGoogle = overlay?.Q<Button>("BtnLoginGoogle");
            
            if (_btnLoginGoogle == null) return;
        }

        _btnLoginGoogle.clicked -= OnLoginGoogleClicked;
        _btnLoginGoogle.clicked += OnLoginGoogleClicked;

        // Check login status initial
        CheckLoginStatusAndToggleUI();
    }

    private void CheckLoginStatusAndToggleUI()
    {
        // [FIX] Use "LoginOverlay" (The HCM-UTE styled one) instead of "LoginContainer"
        var loginOverlay = _uiRoot?.Q<VisualElement>("LoginOverlay");
        if (loginOverlay == null) return;

        bool loggedIn = !string.IsNullOrEmpty(_currentTeacherId);
        
        // If logged in -> Hide Overlay. If not -> Show Overlay (Flex)
        loginOverlay.style.display = loggedIn ? DisplayStyle.None : DisplayStyle.Flex;
    }

    private void OnLoginGoogleClicked()
    {
        StartCoroutine(StartGoogleLoginFlow());
    }

    private IEnumerator StartGoogleLoginFlow()
    {
        string url = cloudRunApiUrl.TrimEnd('/') + "/auth/google/start";

        using (var req = UnityWebRequest.Get(url))
        {
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout = 20;

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[GoogleLogin] start failed: " + req.error + " body=" + req.downloadHandler.text);
                ShowToast("❌ Không khởi tạo đăng nhập Google", colRed);
                yield break;
            }

            var text = req.downloadHandler.text ?? "";
            GoogleLoginStartRes res = null;
            try { res = JsonUtility.FromJson<GoogleLoginStartRes>(text); }
            catch { }

            if (res == null || string.IsNullOrEmpty(res.authorization_url) || string.IsNullOrEmpty(res.poll_token))
            {
                Debug.LogError("[GoogleLogin] start parse failed: " + text);
                ShowToast("❌ Response Google login lỗi", colRed);
                yield break;
            }

            Application.OpenURL(res.authorization_url);
            ShowToast("🌐 Đang mở trình duyệt đăng nhập Google...", colOrange);

            // Poll kết quả
            yield return StartCoroutine(PollGoogleLoginFlow(res.poll_token));
        }
    }

    private IEnumerator PollGoogleLoginFlow(string pollToken)
    {
        float start = Time.time;
        float timeoutSec = 180f; // 3 phút
        while (Time.time - start < timeoutSec)
        {
            string pollUrl = cloudRunApiUrl.TrimEnd('/') + "/auth/google/poll?p=" + UnityWebRequest.EscapeURL(pollToken);

            using (var req = UnityWebRequest.Get(pollUrl))
            {
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = 20;

                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    // Debug.LogError("[GoogleLogin] poll failed: " + req.error + " body=" + req.downloadHandler.text);
                    yield return new WaitForSeconds(1.2f);
                    continue;
                }

                var text = req.downloadHandler.text ?? "";
                GoogleLoginPollRes res = null;
                try { res = JsonUtility.FromJson<GoogleLoginPollRes>(text); }
                catch { }

                if (res == null)
                {
                    // Debug.LogError("[GoogleLogin] poll parse failed: " + text);
                    yield return new WaitForSeconds(1.2f);
                    continue;
                }

                if (res.status == "pending")
                {
                    yield return new WaitForSeconds(1.2f);
                    continue;
                }

                if (res.status == "done" && !string.IsNullOrEmpty(res.teacher_id))
                {
                    _currentTeacherId = res.teacher_id;
                    PlayerPrefs.SetString("TeacherId", _currentTeacherId); // Unified key
                    PlayerPrefs.SetString("teacher_id", _currentTeacherId); // Legacy backup
                    PlayerPrefs.Save();

                    _isLoggedIn = true; // Set runtime flag
                    ShowToast("✅ Đăng nhập Google thành công", colGreen);

                    // Refresh data
                    CheckLoginStatusAndToggleUI();
                    StartCoroutine(FetchLogsFromServer(true));
                    InitDashboardStats();
                    UpdateOpenGSheetButton(); // [NEW] Refresh Open Sheet button visibility

                    yield break;
                }

                // error
                ShowToast("❌ Đăng nhập Google thất bại", colRed);
                Debug.LogError("[GoogleLogin] poll error: " + (res.error ?? res.message));
                yield break;
            }
        }

        ShowToast("⌛ Hết thời gian chờ đăng nhập Google", colOrange);
    }

    // [NEW] Setup Stats Tab & Latency Card (Merged into main method)
    // Removed duplicate SetupStatsTab definition

    private static string Fms(float v) => (v > 0f) ? $"{v:0.0} ms" : "—";

    private void UpdateLatencyCardUI()
    {
        if (_lblLatencyTotal == null) return;

        _lblLatencyDetection.text = Fms(_latDetMs);
        _lblLatencyAlignment.text = Fms(_latAlignMs);
        _lblLatencyEmbedding.text = Fms(_latEmbMs);
        _lblLatencyMatching.text = Fms(_latMatchMs);
        _lblLatencyNetwork.text = Fms(_latNetMs);

        _lblLatencyTotal.text = Fms(_latTotalMs);
        if (_lblLatencyNote != null) _lblLatencyNote.text = "Đơn vị: ms (giá trị gần nhất)";
    }

    // --- [NEW] IoU Helper for Tracking ---
    private float CalculateIoU(Rect r1, Rect r2)
    {
        float x1 = Mathf.Max(r1.x, r2.x);
        float y1 = Mathf.Max(r1.y, r2.y);
        float x2 = Mathf.Min(r1.x + r1.width, r2.x + r2.width);
        float y2 = Mathf.Min(r1.y + r1.height, r2.y + r2.height);

        if (x1 >= x2 || y1 >= y2) return 0f;

        float intersection = (x2 - x1) * (y2 - y1);
        float area1 = r1.width * r1.height;
        float area2 = r2.width * r2.height;
        
        return intersection / (area1 + area2 - intersection);
    }
}
// Serializable classes
[System.Serializable]
public class GSheetEnsureResV2
{
    public bool ok;
    public string status;
    public string url;
    public string spreadsheet_url;
    public string message;
    public string error;
    public string created;
    public string spreadsheet_id;
}

[System.Serializable]
public class GoogleLoginInfo
{
    public string authorization_url;
    public string poll_token;
}

[System.Serializable]
public class GoogleLoginStartRes
{
    public string authorization_url;
    public string poll_token;
    public string error;
}

[System.Serializable]
public class GoogleLoginPollRes
{
    public string status;      // "pending" | "done" | "error"
    public string teacher_id;
    public string email;
    public bool has_refresh_token;
    public string message;
    public string error;
}
