using UnityEngine;
using Unity.Netcode; // Necesario para IsOwner

public class PlayerLook : MonoBehaviour
{
    [Header("Local Look Settings")]
    [Tooltip("Referencia al Transform del objeto que contiene la cámara (debe ser hijo del Player)")]
    [SerializeField] private Transform cameraHolder;
    [Tooltip("Sensibilidad del ratón para enviar al servidor")]
    [SerializeField] private float localMouseSensitivity = 1.0f; // Ya no multiplica por 100 aquí, se hace en servidor

    // Referencia al script Player principal para llamar al RPC
    private Player playerScript;
    private NetworkObject networkObject; // Para verificar IsOwner


    void Awake()
    {
        // Obtenemos las referencias necesarias
        playerScript = GetComponent<Player>();
        networkObject = GetComponent<NetworkObject>();

        if (playerScript == null || networkObject == null)
        {
             Debug.LogError("PlayerLook necesita los componentes Player y NetworkObject en el mismo GameObject.", this);
             this.enabled = false;
             return;
        }
         if (cameraHolder == null)
        {
            // Intenta encontrarlo si no está asignado
            cameraHolder = transform.Find("CameraHolder");
            if (cameraHolder == null)
            {
                Debug.LogError("Referencia a CameraHolder no asignada ni encontrada como hijo en PlayerLook.", this);
                this.enabled = false;
                return;
            }
        }
    }

    void Start()
    {
        // Solo el dueño debe bloquear el cursor
        if (networkObject.IsOwner)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    void Update()
    {
        // Solo el dueño del objeto procesa y envía el input
        if (!networkObject.IsOwner)
        {
            return; // Si no somos el dueño, no hacemos nada en Update
        }

        // --- Obtener Input del Mouse (Solo el Dueño) ---
        // NO multiplicamos por Time.deltaTime aquí, el servidor lo hará para consistencia.
        // Enviamos el delta "crudo" multiplicado por una sensibilidad local si se desea ajustar la escala del input.
        float mouseX = Input.GetAxis("Mouse X") * localMouseSensitivity;
        float mouseY = Input.GetAxis("Mouse Y") * localMouseSensitivity;

        // --- Enviar Input al Servidor ---
        if (playerScript != null)
        {
            // Llamamos al ServerRpc definido en Player.cs
            playerScript.UpdateLookInputServerRpc(mouseX, mouseY);
        }

        // --- IMPORTANTE: Ya NO aplicamos rotación directamente aquí ---
        // transform.Rotate(Vector3.up * mouseX); // <- ELIMINADO
        // verticalRotation -= mouseY; // <- ELIMINADO
        // verticalRotation = Mathf.Clamp(verticalRotation, minVerticalAngle, maxVerticalAngle); // <- ELIMINADO
        // cameraHolder.localRotation = Quaternion.Euler(verticalRotation, 0f, 0f); // <- ELIMINADO
    }

    // --- NUEVO MÉTODO PÚBLICO ---
    // Este método es llamado por Player.cs (HandleVerticalRotationChanged)
    // cuando la NetworkVariable de rotación vertical cambia.
    public void SetVerticalRotationLocally(float verticalAngle)
    {
        if (cameraHolder != null)
        {
            // Aplicamos la rotación vertical recibida desde el estado del servidor
            // directamente al CameraHolder local.
            cameraHolder.localRotation = Quaternion.Euler(verticalAngle, 0f, 0f);
        }
    }

     void OnDestroy() {
         // Desbloquear cursor si somos dueños al destruir
         if (networkObject != null && networkObject.IsOwner)
         {
             Cursor.lockState = CursorLockMode.None;
             Cursor.visible = true;
         }
     }
}
