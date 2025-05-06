using UnityEngine;
using Unity.Netcode;

// Enum está ahora fuera de Player, así que lo referenciamos directamente.

[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Collider))] // DEBE ser Trigger
public class PowerUp : NetworkBehaviour
{
   
    public NetworkVariable<PowerUpType> Type = new NetworkVariable<PowerUpType>(
        PowerUpType.Speed, // Valor inicial por defecto

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

    public void Initialize(PowerUpType type)
    {
        if (!IsServer) return;
        Type.Value = type;
    }

    // Actualiza visuales cuando el tipo cambia
    private void OnTypeChanged(PowerUpType previousValue, PowerUpType newValue)
    {
        if (speedVisual != null) speedVisual.SetActive(newValue == PowerUpType.Speed);
        if (fireRateVisual != null) fireRateVisual.SetActive(newValue == PowerUpType.FireRate);
        if (healthVisual != null) healthVisual.SetActive(newValue == PowerUpType.Health);
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


    public PowerUpType GetPowerUpType() => Type.Value;
    public bool CanBePickedUp() => canBePickedUp;
}
