using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace LunaBgmLibrary
{
    public partial class EqualizerWindow : Window
    {
        private readonly List<Slider> _sliders;
        private readonly List<TextBlock> _labels;
        private readonly float[] _centers = new float[] { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

        private bool _isInitializing = false;

        public string SelectedPresetName { get; private set; } = "Custom";
        public List<EqBandSetting> Bands { get; private set; }

        public EqualizerWindow(List<EqBandSetting> currentBands, string presetName)
        {
            InitializeComponent();
            _sliders = new() { B0, B1, B2, B3, B4, B5, B6, B7, B8, B9 };
            _labels  = new() { B0V, B1V, B2V, B3V, B4V, B5V, B6V, B7V, B8V, B9V };

            foreach (var p in EqDefaults.Presets) PresetBox.Items.Add(p.Item1);
            PresetBox.Items.Add("Custom");

            Bands = EqDefaults.CreateDefault10Band();
            if (currentBands != null && currentBands.Count > 0)
            {
                int n = Math.Min(10, currentBands.Count);
                for (int i = 0; i < n; i++)
                {
                    Bands[i].Frequency = _centers[i];
                    Bands[i].GainDb    = (float)Math.Clamp(currentBands[i].GainDb, -12.0, 12.0);
                    Bands[i].Q         = (currentBands[i].Q <= 0 ? 1.0f : currentBands[i].Q);
                }
            }
            SelectedPresetName = string.IsNullOrWhiteSpace(presetName) ? "Custom" : presetName;

            _isInitializing = true;
            for (int i = 0; i < 10; i++)
            {
                _sliders[i].Minimum = -12;
                _sliders[i].Maximum =  12;
                _sliders[i].TickFrequency = 1;
                _sliders[i].IsSnapToTickEnabled = true;

                _sliders[i].Value = Bands[i].GainDb;
                _labels[i].Text   = $"{Bands[i].GainDb:0.#} dB";
            }
            var match = EqDefaults.Presets.FirstOrDefault(p => string.Equals(p.Item1, SelectedPresetName, StringComparison.OrdinalIgnoreCase));
            PresetBox.SelectedItem = string.IsNullOrEmpty(match.Item1) ? "Custom" : match.Item1;
            _isInitializing = false;

            SyncBandsFromSliders();
        }

        private void SyncBandsFromSliders()
        {
            for (int i = 0; i < 10; i++)
            {
                Bands[i].Frequency = _centers[i];
                Bands[i].GainDb    = (float)Math.Clamp(_sliders[i].Value, -12.0, 12.0);
                Bands[i].Q         = (Bands[i].Q <= 0 ? 1.0f : Bands[i].Q);
                _labels[i].Text    = $"{Bands[i].GainDb:0.#} dB";
            }
        }

        private void Band_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInitializing) return;
            SyncBandsFromSliders();
            if (!string.Equals(PresetBox.SelectedItem?.ToString(), "Custom", StringComparison.Ordinal))
            {
                SelectedPresetName = "Custom";
                PresetBox.SelectedItem = "Custom";
            }
        }

        private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            var name = PresetBox.SelectedItem?.ToString() ?? "Custom";
            if (name == "Custom") { SelectedPresetName = "Custom"; return; }

            var preset = EqDefaults.Presets.FirstOrDefault(p => string.Equals(p.Item1, name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(preset.Item1) && preset.Item2 != null && preset.Item2.Count == 10)
            {
                SelectedPresetName = preset.Item1;
                _isInitializing = true;
                for (int i = 0; i < 10; i++)
                {
                    Bands[i].Frequency = _centers[i];
                    Bands[i].GainDb    = (float)Math.Clamp(preset.Item2[i].GainDb, -12.0, 12.0);
                    Bands[i].Q         = (preset.Item2[i].Q <= 0 ? 1.0f : preset.Item2[i].Q);
                    _sliders[i].Value  = Bands[i].GainDb;
                    _labels[i].Text    = $"{Bands[i].GainDb:0.#} dB";
                }
                _isInitializing = false;
                SyncBandsFromSliders();
            }
        }

        private void ResetBtn_Click(object sender, RoutedEventArgs e)
        {
            var flat = EqDefaults.Flat();
            SelectedPresetName = flat.Item1;

            _isInitializing = true;
            for (int i = 0; i < 10; i++)
            {
                Bands[i].Frequency = _centers[i];
                Bands[i].GainDb = 0f;
                Bands[i].Q = 1.0f;
                _sliders[i].Value = 0;
                _labels[i].Text = "0 dB";
            }
            PresetBox.SelectedItem = flat.Item1;
            _isInitializing = false;

            SyncBandsFromSliders();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            SyncBandsFromSliders();
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
