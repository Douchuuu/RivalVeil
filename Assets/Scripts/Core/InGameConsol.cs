using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Внутриигровая консоль для BUILD-клиента.
/// F1 — показать/скрыть
/// F3 — очистить
/// Перехватывает все Debug.Log / LogWarning / LogError
/// </summary>
public class IngameConsole : MonoBehaviour
{
    private static IngameConsole _instance;

    [Header("Настройки")]
    [SerializeField] private int maxLines = 150;
    [SerializeField] private int fontSize = 13;

    private readonly List<LogEntry> _logs = new List<LogEntry>();
    private Vector2 _scroll;
    private bool _visible = false;

    private GUIStyle _styleNormal;
    private GUIStyle _styleWarning;
    private GUIStyle _styleError;
    private GUIStyle _styleBox;
    private bool _stylesInit = false;

    private struct LogEntry
    {
        public string text;
        public LogType type;
    }

    // ─────────────────────────────────────────────────────────────────────────

    void Awake()
    {
        if (_instance != null) { Destroy(gameObject); return; }
        _instance = this;
        DontDestroyOnLoad(gameObject);
        Application.logMessageReceived += HandleLog;
    }

    void OnDestroy()
    {
        Application.logMessageReceived -= HandleLog;
        if (_instance == this) _instance = null;
    }

    void HandleLog(string message, string stackTrace, LogType type)
    {
        string prefix = type switch
        {
            LogType.Warning => "⚠️ ",
            LogType.Error => "❌ ",
            LogType.Exception => "💥 ",
            LogType.Assert => "❗ ",
            _ => ""
        };

        _logs.Add(new LogEntry { text = prefix + message, type = type });

        // Для ошибок добавляем первую строку стектрейса
        if ((type == LogType.Error || type == LogType.Exception) && !string.IsNullOrEmpty(stackTrace))
        {
            string firstLine = stackTrace.Split('\n')[0].Trim();
            if (!string.IsNullOrEmpty(firstLine))
                _logs.Add(new LogEntry { text = "   ↳ " + firstLine, type = type });
        }

        if (_logs.Count > maxLines)
            _logs.RemoveRange(0, _logs.Count - maxLines);

        // Автоскролл вниз
        _scroll.y = float.MaxValue;
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.F1))
            _visible = !_visible;

        if (Input.GetKeyDown(KeyCode.F3))
        {
            _logs.Clear();
            Debug.Log("[Console] Очищено");
        }
    }

    void InitStyles()
    {
        if (_stylesInit) return;
        _stylesInit = true;

        _styleBox = new GUIStyle(GUI.skin.box)
        {
            normal = { background = MakeTex(2, 2, new Color(0f, 0f, 0f, 0.88f)) }
        };

        _styleNormal = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize,
            wordWrap = true,
            richText = true,
            normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
        };

        _styleWarning = new GUIStyle(_styleNormal)
        {
            normal = { textColor = new Color(1f, 0.85f, 0.2f) }
        };

        _styleError = new GUIStyle(_styleNormal)
        {
            normal = { textColor = new Color(1f, 0.35f, 0.35f) }
        };
    }

    void OnGUI()
    {
        if (!_visible) return;

        InitStyles();

        float w = Screen.width * 0.75f;
        float h = Screen.height * 0.55f;
        float x = (Screen.width - w) * 0.5f;
        float y = 10f;

        GUI.Box(new Rect(x - 4, y - 4, w + 8, h + 8), GUIContent.none, _styleBox);

        // Заголовок
        GUI.Label(
            new Rect(x, y, w - 110, 22),
            $"<b>[CONSOLE]  F1=скрыть  F3=очистить  ({_logs.Count} строк)</b>",
            _styleNormal);

        // Кнопка копирования всех логов
        if (GUI.Button(new Rect(x + w - 105, y, 100, 22), "Copy All"))
        {
            var sb = new System.Text.StringBuilder();
            foreach (var entry in _logs)
                sb.AppendLine(entry.text);
            GUIUtility.systemCopyBuffer = sb.ToString();
            Debug.Log($"[Console] Скопировано {_logs.Count} строк в буфер обмена");
        }

        // Область скролла
        Rect scrollRect = new Rect(x, y + 24, w, h - 24);
        float contentH = _logs.Count * (fontSize + 5) + 10;

        _scroll = GUI.BeginScrollView(scrollRect, _scroll,
            new Rect(0, 0, w - 20, Mathf.Max(contentH, scrollRect.height)));

        float cy = 4f;
        foreach (var entry in _logs)
        {
            GUIStyle style = entry.type switch
            {
                LogType.Warning => _styleWarning,
                LogType.Error => _styleError,
                LogType.Exception => _styleError,
                _ => _styleNormal
            };
            float lineH = style.CalcHeight(new GUIContent(entry.text), w - 20);
            GUI.Label(new Rect(4, cy, w - 24, lineH), entry.text, style);
            cy += lineH + 2;
        }

        GUI.EndScrollView();
    }

    private Texture2D MakeTex(int width, int height, Color col)
    {
        var tex = new Texture2D(width, height);
        var pixels = new Color[width * height];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = col;
        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }
}