using UnityEngine;
using Unity.Mathematics;

namespace Microverse.Scripts.Simulation
{
    public class PlayerController : MonoBehaviour
    {
        [Header("Player")]
        public bool enablePlayer = true;
        public float radius = 0.001f;
        public float speed = 6f;
        public bool wrapEdges = false;

        [Header("World Ref")]
        public Vector2 worldSize = new Vector2(20, 11.25f);

        [Header("Health")]
        public float health = 10000f;
        public float maxHealth = 10000f;

        // ── 위치
        private float2 _pos;
        public float2 Pos => _pos;

        // ── 플레이어 속도(라치 해제용으로 추정)
        private float2 _prevPos;
        public float2 Vel { get; private set; }

        void Start()
        {
            _pos = float2.zero;
            _prevPos = _pos;
        }

        void Update()
        {
            if (!enablePlayer) return;

            // 속도 갱신
            Vel = (_pos - _prevPos) / math.max(1e-6f, Time.deltaTime);
            _prevPos = _pos;

            // 입력
            float2 mv = new float2(Input.GetAxisRaw("Horizontal"),
                                   Input.GetAxisRaw("Vertical"));
            if (math.lengthsq(mv) > 1e-6f) mv = math.normalize(mv);

            _pos += mv * speed * Time.deltaTime;

            // 경계 처리
            if (wrapEdges)
            {
                _pos = new float2(
                    Wrap01(_pos.x, worldSize.x),
                    Wrap01(_pos.y, worldSize.y)
                );
            }
            else
            {
                float hx = worldSize.x * 0.5f, hy = worldSize.y * 0.5f;
                _pos.x = math.clamp(_pos.x, -hx + radius, hx - radius);
                _pos.y = math.clamp(_pos.y, -hy + radius, hy - radius);
            }

            // (선택) 체력 0일 때 사망 처리 예시
            if (health <= 0f)
            {
                enablePlayer = false;
                Debug.Log(" Player died");
            }

            transform.localScale = Vector3.one * (radius * 2f);

        }

        // ── 외부에서 위치 변경하고 싶을 때 쓰는 안전 API
        public void Teleport(float2 p) => _pos = p;
        public void Nudge(float2 delta) => _pos += delta;

        // ── 체력 조작 유틸
        public void ApplyDamage(float dmg)
        {
            health = math.max(0f, health - dmg);
        }

        public void Heal(float amount)
        {
            health = math.min(maxHealth, health + amount);
        }

        static float Wrap01(float x, float range)
        {
            float half = range * 0.5f;
            if (x < -half) x += range;
            else if (x > half) x -= range;
            return x;
        }
    }
}
