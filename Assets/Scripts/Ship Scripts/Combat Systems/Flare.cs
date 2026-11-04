using UnityEngine;
using UnityEngine.VFX;

public class Flare : MonoBehaviour
{
    [Header("Attributes")]
    [SerializeField] private float speed = 10f;
    [SerializeField] private float chaffStrength = 10f;
    [SerializeField] private float flareStrength = 10f;
    [SerializeField] private float deceleration = 3f;

    [Header("VFX")]
    [SerializeField] private Transform FlareVFX;

    private VisualEffect flareTrail;
    private SpriteRenderer spriteRenderer;
    private Rigidbody2D rb;
    private int smokeVelocityID;
    private int smokeSizeID;

    private Color baseColor;
    private float totalLifetime;
    private float spawnTime;
    private float initialSmokeSize;
    private float smokeLifetime;
    private float effectTime;
    private float currentT;
    private bool stopped;

    private void Awake()
    {
        flareTrail = FlareVFX.GetComponent<VisualEffect>();
        spriteRenderer = GetComponentInChildren<SpriteRenderer>();
        rb = GetComponent<Rigidbody2D>();
        smokeVelocityID = Shader.PropertyToID("SmokeVelocity");
        smokeSizeID = Shader.PropertyToID("SmokeSize");
        baseColor = spriteRenderer.color;
        initialSmokeSize = flareTrail.GetFloat(smokeSizeID);
        smokeLifetime = flareTrail.GetFloat(Shader.PropertyToID("SmokeLifetime"));
    }

    // Pool return is driven by effect time, not wall time: the smoke trail simulates at most
    // VFXManager.maxDeltaTime per frame, so a wall-clock expiry would recycle the flare with its
    // smoke still mid-life whenever frames run long.
    private void Update()
    {
        effectTime += Mathf.Min(Time.deltaTime, UnityEngine.VFX.VFXManager.maxDeltaTime);
        if (effectTime >= totalLifetime)
            Despawn();
    }

    private void FixedUpdate()
    {

        float currentSpeed = rb.linearVelocity.magnitude;

        if (currentSpeed > 0f)
        {
            float newSpeed = Mathf.Max(0f, currentSpeed - deceleration * Time.fixedDeltaTime);
            rb.linearVelocity = rb.linearVelocity.normalized * newSpeed;
        }

        if (currentSpeed <= 0f && !stopped)
        {
            flareTrail.Stop();
            stopped = true;
        }

        float elapsed = Time.time - spawnTime;
        currentT = Mathf.Clamp01(elapsed / totalLifetime);

        // Fade sprite opacity over the total lifetime
        baseColor.a = 1f - currentT;
        spriteRenderer.color = baseColor;

        flareTrail.SetFloat(smokeSizeID, initialSmokeSize * (1f - currentT));

        // Update smoke trail to trail behind flare movement
        if (currentSpeed > 0.1f)
        {
            Vector2 trailDir = -rb.linearVelocity.normalized;
            flareTrail.SetVector3(smokeVelocityID, new Vector3(trailDir.x, trailDir.y, 0f));
        }
    }

    public void Setup(Vector2 direction, Vector2 shipVelocity, ShipBase source)
    {
        rb.linearVelocity = direction.normalized * speed;
        spawnTime = Time.time;

        float initialSpeed = rb.linearVelocity.magnitude;
        float timeToStop = initialSpeed / deceleration;
        totalLifetime = timeToStop + smokeLifetime;

        // Reset state a recycled flare carries over from its previous life.
        currentT = 0f;
        stopped = false;
        baseColor.a = 1f;
        spriteRenderer.color = baseColor;
        flareTrail.SetFloat(smokeSizeID, initialSmokeSize);
        flareTrail.Reinit();
        flareTrail.SendEvent("OnDamage");
        effectTime = 0f;
    }

    private void Despawn()
    {
        ObjectPoolManager.ReturnObjectToPool(gameObject, ObjectPoolManager.PoolType.Flare);
    }

    public float GetChaffStrength() => chaffStrength * (1f - currentT);

    public float GetFlareStrength() => flareStrength * (1f - currentT);
}
