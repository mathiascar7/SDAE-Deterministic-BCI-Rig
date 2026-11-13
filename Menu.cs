using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;
using UnityEngine.UI;
using System;
using System.Globalization;
using System.IO;

[Serializable]
public class Settings
{
    public string sceneToLoad = "TerrainDemoScene1";
    public float fallSpeedPercentage = 100f;
    public float predictionPercentile = 50f;
    public int subjectNumber = 1;
    public bool trainingMode = true;
    public bool showTestUI = true;
}

public class Menu : MonoBehaviour
{
    [Header("UI Fields (Opcional para sincronizar valores al iniciar)")]
    [SerializeField] private TMP_InputField sceneField;
    [SerializeField] private TMP_InputField fallSpeedField;     // Apple Speed %
    [SerializeField] private TMP_InputField subjectNumberField; // Subject Number
    [SerializeField] private TMP_InputField sendIntervalField;  // Prediction %
    [SerializeField] private Toggle myToggle;                   // Training Toggle
    [SerializeField] private Toggle testUIToggle;               // Test UI Toggle

    private Settings settings = new Settings();
    private const string SETTINGS_FILE_NAME = "menu_settings_v2.json";

    private void Start()
    {
        LoadSettingsFromFile();
        ApplySettingsToUI();
        ApplySettingsToRuntime();
    }

    // =========================================================================
    // MÉTODOS DINÁMICOS LLAMABLES DESDE EL INSPECTOR (Dynamic string / bool)
    // =========================================================================

    /// <summary>
    /// Asignar en Apple Speed % -> On Value Changed (String) o On End Edit (String) [Dynamic string]
    /// </summary>
    public void SetFallSpeedPercentage(string value)
    {
        if (TryParseFloat(value, out float v))
        {
            settings.fallSpeedPercentage = Mathf.Clamp(v, 10f, 300f);
            ApplySettingsToRuntime();
        }
    }

    /// <summary>
    /// Asignar en Subject Number -> On Value Changed (String) o On End Edit (String) [Dynamic string]
    /// </summary>
    public void SetSubjectNumber(string value)
    {
        if (int.TryParse(value?.Trim(), out int n))
        {
            settings.subjectNumber = Mathf.Max(1, n);
            ApplySettingsToRuntime();
        }
    }

    /// <summary>
    /// Asignar en Prediction % -> On Value Changed (String) o On End Edit (String) [Dynamic string]
    /// </summary>
    public void SetPredictionPercentile(string value)
    {
        if (TryParseFloat(value, out float v))
        {
            settings.predictionPercentile = Mathf.Clamp(v, 10f, 95f);
            ApplySettingsToRuntime();
        }
    }

    /// <summary>
    /// Asignar en Toggle Training -> On Value Changed (Boolean) [Dynamic bool]
    /// </summary>
    public void SetTrainingMode(bool isTraining)
    {
        settings.trainingMode = isTraining;
        ApplySettingsToRuntime();
    }

    /// <summary>
    /// Asignar en Toggle Test UI -> On Value Changed (Boolean) [Dynamic bool]
    /// </summary>
    public void SetTestUIVisible(bool isVisible)
    {
        settings.showTestUI = isVisible;
        ApplySettingsToRuntime();
    }

    /// <summary>
    /// Asignar si tienes un InputField para el nombre de la escena [Dynamic string]
    /// </summary>
    public void SetSceneToLoad(string sceneName)
    {
        if (!string.IsNullOrWhiteSpace(sceneName))
        {
            settings.sceneToLoad = sceneName.Trim();
        }
    }

    // =========================================================================
    // MÉTODOS SIN PARÁMETROS (Static Parameters / Botones Play y Save)
    // =========================================================================

    public void UpdateFallSpeedFromField()
    {
        if (fallSpeedField != null) SetFallSpeedPercentage(fallSpeedField.text);
    }

    public void UpdateSubjectNumberFromField()
    {
        if (subjectNumberField != null) SetSubjectNumber(subjectNumberField.text);
    }

    public void UpdatePredictionPercentileFromField()
    {
        if (sendIntervalField != null) SetPredictionPercentile(sendIntervalField.text);
    }

    public void UpdateTrainingFromToggle()
    {
        if (myToggle != null) SetTrainingMode(myToggle.isOn);
    }

    public void UpdateTestUIFromToggle()
    {
        if (testUIToggle != null) SetTestUIVisible(testUIToggle.isOn);
    }

    public void LoadScene()
    {
        SyncFromUITextDirectly();

        if (string.IsNullOrWhiteSpace(settings.sceneToLoad))
        {
            Debug.LogWarning("[Menu] El nombre de la escena está vacío.");
            return;
        }

        ApplySettingsToRuntime();
        SaveSettingsToFile();

        Debug.Log($"[Menu] Iniciando sesión -> Sujeto #{Apple.subjectNumber} | " +
                  $"Modo: {(Apple.trainingMode ? "TRAINING" : "TESTING")} | " +
                  $"Speed: {Apple.speedPercentage}% | Trigger: {Apple.decisionPercentile}% | " +
                  $"TestUI: {Apple.showTelemetryHUD}");

        SceneManager.LoadScene(settings.sceneToLoad);
    }

    public void SaveSettingsButton()
    {
        SyncFromUITextDirectly();
        ApplySettingsToUI();
        ApplySettingsToRuntime();
        SaveSettingsToFile();
        Debug.Log("[Menu] Configuración guardada correctamente.");
    }

    public void LoadSettingsButton()
    {
        LoadSettingsFromFile();
        ApplySettingsToUI();
        ApplySettingsToRuntime();
        Debug.Log("[Menu] Configuración cargada desde disco.");
    }

    // =========================================================================
    // LÓGICA INTERNA Y PERSISTENCIA
    // =========================================================================

    private bool TryParseFloat(string raw, out float result)
    {
        string clean = raw?.Trim();
        return float.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ||
               float.TryParse(clean, out result);
    }

    private void SyncFromUITextDirectly()
    {
        if (sceneField != null && !string.IsNullOrWhiteSpace(sceneField.text))
            settings.sceneToLoad = sceneField.text.Trim();

        if (fallSpeedField != null && TryParseFloat(fallSpeedField.text, out float spd))
            settings.fallSpeedPercentage = Mathf.Clamp(spd, 10f, 300f);

        if (sendIntervalField != null && TryParseFloat(sendIntervalField.text, out float pct))
            settings.predictionPercentile = Mathf.Clamp(pct, 10f, 95f);

        if (subjectNumberField != null && int.TryParse(subjectNumberField.text?.Trim(), out int subj))
            settings.subjectNumber = Mathf.Max(1, subj);

        if (myToggle != null)
            settings.trainingMode = myToggle.isOn;

        if (testUIToggle != null)
            settings.showTestUI = testUIToggle.isOn;
    }

    private string GetSettingsFilePath() => Path.Combine(Application.persistentDataPath, SETTINGS_FILE_NAME);

    public void SaveSettingsToFile()
    {
        try
        {
            string json = JsonUtility.ToJson(settings, true);
            File.WriteAllText(GetSettingsFilePath(), json);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Menu] Error al guardar configuración: {ex.Message}");
        }
    }

    public void LoadSettingsFromFile()
    {
        string path = GetSettingsFilePath();
        if (!File.Exists(path)) return;

        try
        {
            string json = File.ReadAllText(path);
            Settings loaded = JsonUtility.FromJson<Settings>(json);
            if (loaded != null)
            {
                settings = loaded;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Menu] Error al cargar configuración: {ex.Message}");
        }
    }

    private void ApplySettingsToUI()
    {
        if (sceneField != null) sceneField.text = settings.sceneToLoad;
        if (fallSpeedField != null) fallSpeedField.text = settings.fallSpeedPercentage.ToString(CultureInfo.InvariantCulture);
        if (subjectNumberField != null) subjectNumberField.text = settings.subjectNumber.ToString();
        if (sendIntervalField != null) sendIntervalField.text = settings.predictionPercentile.ToString(CultureInfo.InvariantCulture);
        if (myToggle != null) myToggle.isOn = settings.trainingMode;
        if (testUIToggle != null) testUIToggle.isOn = settings.showTestUI;
    }

    private void ApplySettingsToRuntime()
    {
        Apple.speedPercentage = Mathf.Clamp(settings.fallSpeedPercentage, 10f, 300f);
        Apple.decisionPercentile = Mathf.Clamp(settings.predictionPercentile, 10f, 95f);
        Apple.subjectNumber = Mathf.Max(1, settings.subjectNumber);
        Apple.trainingMode = settings.trainingMode;
        Apple.SetTelemetryHUDVisible(settings.showTestUI);
    }
}