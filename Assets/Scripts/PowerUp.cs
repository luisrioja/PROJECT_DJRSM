using UnityEngine;
using Unity.Netcode;

// Enum está ahora fuera de Player, así que lo referenciamos directamente.

[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Collider))] // DEBE ser Trigger
public class PowerUp : NetworkBehaviour
{
    // --- CORRECCIÓN AQUÍ: Quitar Player. ---
    public NetworkVariable<PowerUpType> Type = new NetworkVariable<PowerUpType>(
        PowerUpType.Speed, // Valor inicial por defecto
    // --- FIN CORRECCIÓN ---
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    [Header("Visuales por Tipo")]
    [SerializeField] private GameObject speedVisual;
    [SerializeField] private GameObject fireRateVisual;
    [SerializeField] private GameObject healthVisual;

    private bool canBePickedUp = true;
    private Collider pickupCollider;

    public override void OnNetworkSpawn()
    {
        pickupCollider = GetComponent<Collider>();
        if (pickupCollider == null || !pickupCollider.isTrigger)
            Debug.LogError($"PowerUp {gameObject.name}: Necesita Collider IsTrigger.");

        Type.OnValueChanged += OnTypeChanged;
        OnTypeChanged(Type.Value, Type.Value); // Update inicial
    }

     public override void OnNetworkDespawn() {
         if (Type != null) Type.OnValueChanged -= OnTypeChanged;
         base.OnNetworkDespawn();
     }

    // Llamado por PowerUpManager en servidor ANTES de Spawn()
    // --- CORRECCIÓN AQUÍ: Quitar Player. ---
    public void Initialize(PowerUpType type)
    // --- FIN CORRECCIÓN ---
    {
        if (!IsServer) return;
        Type.Value = type;
    }

    // Actualiza visuales cuando el tipo cambia
    // --- CORRECCIÓN AQUÍ: Quitar Player. ---
    private void OnTypeChanged(PowerUpType previousValue, PowerUpType newValue)
    // --- FIN CORRECCIÓN ---
    {
        // --- CORRECCIÓN AQUÍ: Quitar Player. ---
        if (speedVisual != null) speedVisual.SetActive(newValue == PowerUpType.Speed);
        if (fireRateVisual != null) fireRateVisual.SetActive(newValue == PowerUpType.FireRate);
        if (healthVisual != null) healthVisual.SetActive(newValue == PowerUpType.Health);
        // --- FIN CORRECCIÓN ---
    }

    // Detección de recogida (solo en el servidor)
    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer || !canBePickedUp) return;

        Player player = other.GetComponent<Player>();
        if (player != null && !player.IsDead.Value)
        {
            canBePickedUp = false;

            // Llamar al método público en Player (esto está bien)
            player.ServerHandlePowerUpPickup(Type.Value);

            if (NetworkObject != null && NetworkObject.IsSpawned)
            {
                 NetworkObject.Despawn(true);
            }
        }
    }

    // Bloque comentado de lógica alternativa (no usar preferiblemente)
    /* ... */

    // --- CORRECCIÓN AQUÍ: Quitar Player. ---
    public PowerUpType GetPowerUpType() => Type.Value;
    // --- FIN CORRECCIÓN ---
    public bool CanBePickedUp() => canBePickedUp;
}
