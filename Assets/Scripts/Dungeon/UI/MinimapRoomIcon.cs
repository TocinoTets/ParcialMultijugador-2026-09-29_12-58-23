using UnityEngine;
using UnityEngine.UI;

namespace DungeonSystem
{
    /// <summary>
    /// Icono del minimapa para una sala. Guarda su color base para poder cambiar
    /// la opacidad (salas visitadas / adyacentes / ocultas) y el brillo de la
    /// sala actual sin perder el color del tipo de sala.
    /// </summary>
    [DisallowMultipleComponent]
    public class MinimapRoomIcon : MonoBehaviour
    {
        [SerializeField] private Image image;

        private Color _baseColor = Color.white;
        private float _visibility = 1f;
        private float _brightnessBoost;

        private void Awake()
        {
            if (image == null) image = GetComponent<Image>();
        }

        public void Setup(Sprite sprite, Color color)
        {
            if (image == null) image = GetComponent<Image>();
            if (image == null) return;

            image.sprite = sprite;
            image.enabled = true;

            _baseColor = color;
            Apply();
        }

        /// <summary>0 = totalmente oculta, 1 = opacidad de sala visitada.</summary>
        public void SetVisibility(float alpha)
        {
            _visibility = Mathf.Clamp01(alpha);
            Apply();
        }

        public void SetHighlighted(bool highlighted, float boost)
        {
            _brightnessBoost = highlighted ? Mathf.Max(0f, boost) : 0f;
            Apply();
        }

        private void Apply()
        {
            if (image == null) return;

            Color color = _baseColor;

            // El brillo de la sala actual se aplica sobre RGB, no sobre el alfa:
            // la sala no deja de estar visitada por estar resaltada.
            color.r = Mathf.Clamp01(color.r + _brightnessBoost);
            color.g = Mathf.Clamp01(color.g + _brightnessBoost);
            color.b = Mathf.Clamp01(color.b + _brightnessBoost);
            color.a = _baseColor.a * _visibility;

            image.color = color;
        }
    }
}
