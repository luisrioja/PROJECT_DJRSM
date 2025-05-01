using UnityEngine;
using Unity.Netcode;

[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Collider))] // Para detectar la interacción con OverlapSphere
public class Door : NetworkBehaviour
{
    [Header("Configuración")]
    [SerializeField] private Transform doorVisualTransform; // El objeto que realmente se mueve/rota
    [SerializeField] private Vector3 closedRotationEuler;   // Rotación Euler cuando está cerrada
    [SerializeField] private Vector3 openRotationEuler;     // Rotación Euler cuando está abierta
    [SerializeField] private float animationSpeed = 2.0f;   // Velocidad de la animación de apertura/cierre

    // Sincronizar el estado de la puerta
    public NetworkVariable<bool> IsOpen = new NetworkVariable<bool>(
        false, // Valor inicial (cerrada)
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server); // Solo el servidor cambia el estado

    private Quaternion targetRotation; // Rotación objetivo para la animación

    public override void OnNetworkSpawn()
    {
        if (doorVisualTransform == null)
        {
            Debug.LogError($"Puerta {gameObject.name}: No se ha asignado 'Door Visual Transform'.");
            enabled = false; // Desactivar si falta la referencia clave
            return;
        }

        // Suscribirse al cambio de estado
        IsOpen.OnValueChanged += OnStateChanged;

        // Establecer estado visual inicial basado en el valor de red
        OnStateChanged(IsOpen.Value, IsOpen.Value); // Llama al callback con el valor actual
    }

    public override void OnNetworkDespawn()
    {
         // Desuscribirse
         if (IsOpen != null) IsOpen.OnValueChanged -= OnStateChanged;
         base.OnNetworkDespawn();
    }

    private void OnStateChanged(bool previousValue, bool newValue)
    {
        // Actualizar la rotación objetivo cuando el estado cambia
        targetRotation = Quaternion.Euler(newValue ? openRotationEuler : closedRotationEuler);
        Debug.Log($"Puerta {gameObject.name}: Estado cambiado a {(newValue ? "Abierta" : "Cerrada")}. Actualizando targetRotation.");
    }

    void Update()
    {
        // Animar suavemente la puerta hacia la rotación objetivo en todos los clientes y servidor
        if (doorVisualTransform != null)
        {
            doorVisualTransform.localRotation = Quaternion.Slerp(
                doorVisualTransform.localRotation,
                targetRotation,
                Time.deltaTime * animationSpeed
            );
        }
    }

    // Llamado por Player.cs (SubmitInteractionRequestServerRpc) en el servidor
    public void ToggleDoorState()
    {
        if (!IsServer)
        {
            Debug.LogWarning("ToggleDoorState llamado desde un no-servidor.");
            return;
        }

        // Cambiar el estado
        bool newValue = !IsOpen.Value; // Calcula cuál será el nuevo valor
        IsOpen.Value = newValue;       // Asigna el nuevo valor (esto disparará OnStateChanged)


        Debug.Log($"Puerta {gameObject.name} (Servidor): Cambiando estado a {(newValue ? "Abierta" : "Cerrada")} (IsOpen.Value: {IsOpen.Value})");

    }

}

