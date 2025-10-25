using UnityEngine;
using Unity.Mathematics;

namespace Microverse.Scripts.Simulation
{
    public class PlayerController : MonoBehaviour
    {
        [Header("Player")]
        public bool enablePlayer = true;
        public float radius = 0.2f;
        public float speed = 6f;
        public bool wrapEdges = false;

        [Header("World Ref")]
        public Vector2 worldSize = new Vector2(20, 11.25f);

        // ── 위치: 외부는 읽기만, 변경은 메서드로
        private float2 _pos;
        public float2 Pos => _pos;

        void Start()
        {
            _pos = float2.zero;
        }

        void Update()
        {
            if (!enablePlayer) return;//f

            // 구 입력 시스템(axes). 새 Input System이면 여기만 교체
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
        }

        // ── 외부에서 위치 변경하고 싶을 때 쓰는 안전 API
        public void Teleport(float2 p) => _pos = p;
        public void Nudge(float2 delta) => _pos += delta;

        static float Wrap01(float x, float range)
        {
            float half = range * 0.5f;
            if (x < -half) x += range;
            else if (x > half) x -= range;
            return x;
        }
    }
}
