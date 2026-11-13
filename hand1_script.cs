using UnityEngine;

public class hand1_script : MonoBehaviour
{
    [Header("Animator Reference")]
    public Animator animator;
    public static Transform handTransform;

    [Header("Collider Reference")]
    [Tooltip("Asigna el MeshCollider de la mano o el Collider del hueso de la palma")]
    public Collider handCollider;

    [Header("Animation State Flags (Live Telemetry)")]
    [Tooltip("Indica si la mano completó la animación de apertura y está en reposo")]
    public bool isFullyOpen = true;
    [Tooltip("Indica si la mano completó la animación de cierre y está en reposo")]
    public bool isFullyClosed = false;
    [Tooltip("Indica si la mano está actualmente en transición o moviendo sus dedos")]
    public bool isAnimating = false;

    [Header("Animator State Names Configuration")]
    [Tooltip("Nombre exacto del estado 'Abierto' en la capa 0 del Animator Controller")]
    public string openStateName = "Open";
    [Tooltip("Nombre exacto del estado 'Cerrado' en la capa 0 del Animator Controller")]
    public string closeStateName = "Close";
    [Tooltip("Nombre del parámetro Bool en el Animator Controller")]
    public string closeParamName = "CloseHand";
    [Tooltip("Umbral de finalización del clip (0.95 = 95% completado)")]
    public float completionThreshold = 0.95f;

    void Awake()
    {
        if (animator == null)
        {
            animator = GetComponent<Animator>();
        }

        handTransform = transform;

        // Búsqueda automática del MeshCollider o Collider general
        if (handCollider == null)
        {
            handCollider = GetComponentInChildren<MeshCollider>();
            if (handCollider == null)
            {
                handCollider = GetComponentInChildren<Collider>();
            }
        }
    }

    void Start()
    {
        UpdateAnimationFlags();
    }

    void Update()
    {
        UpdateAnimationFlags();
    }

    /// <summary>
    /// Detecta impactos físicos si 'Is Trigger' está DESACTIVADO en el MeshCollider
    /// </summary>
    void OnCollisionEnter(Collision collision)
    {
        Debug.Log($"<color=#00FFFF>[HAND 1 MESH COLLIDER - PHYSIC]</color> ¡Colisión detectada con: '{collision.gameObject.name}'! (Tag: {collision.gameObject.tag})");
    }

    /// <summary>
    /// Detecta intercepciones si 'Is Trigger' está ACTIVADO en el MeshCollider
    /// </summary>
    void OnTriggerEnter(Collider other)
    {
        Debug.Log($"<color=#FF00FF>[HAND 1 MESH COLLIDER - TRIGGER]</color> ¡Trigger detectado con: '{other.gameObject.name}'! (Tag: {other.gameObject.tag})");
    }

    /// <summary>
    /// Monitorea en cada frame el estado interno de Mecanim (Layer 0)
    /// </summary>
    private void UpdateAnimationFlags()
    {
        if (animator == null) return;

        bool inTransition = animator.IsInTransition(0);

        if (inTransition)
        {
            isAnimating = true;
            isFullyOpen = false;
            isFullyClosed = false;
            return;
        }

        AnimatorStateInfo stateInfo = animator.GetCurrentAnimatorStateInfo(0);

        if (stateInfo.IsName(openStateName) && stateInfo.normalizedTime >= completionThreshold)
        {
            isFullyOpen = true;
            isFullyClosed = false;
            isAnimating = false;
        }
        else if (stateInfo.IsName(closeStateName) && stateInfo.normalizedTime >= completionThreshold)
        {
            isFullyOpen = false;
            isFullyClosed = true;
            isAnimating = false;
        }
        else
        {
            isAnimating = true;
            isFullyOpen = false;
            isFullyClosed = false;
        }
    }

    public void OpenHand()
    {
        isFullyOpen = false;
        isFullyClosed = false;
        isAnimating = true;

        if (animator != null)
        {
            animator.SetBool(closeParamName, false);
        }
    }

    public void CloseHand()
    {
        isFullyOpen = false;
        isFullyClosed = false;
        isAnimating = true;

        if (animator != null)
        {
            animator.SetBool(closeParamName, true);
        }
    }

    public void OnAnimationOpenEnd()
    {
        isFullyOpen = true;
        isFullyClosed = false;
        isAnimating = false;
    }

    public void OnAnimationCloseEnd()
    {
        isFullyOpen = false;
        isFullyClosed = true;
        isAnimating = false;
    }
}