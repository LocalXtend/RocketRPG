#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using RocketRPG.Models;

namespace RocketRPG.Views;

public partial class DataInspectorWindow : Window
{
    private readonly IGameBridge? _renderer;
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(1000) };   // 게임이 수천 개를 덤프하므로 1초면 충분

    private ObservableCollection<SwitchItem> _switches = new();
    private ObservableCollection<VariableItem> _variables = new();

    private ICollectionView? _switchView;
    private ICollectionView? _varView;

    public DataInspectorWindow(IGameBridge? renderer = null)
    {
        InitializeComponent();
        _renderer = renderer;

        _switchView = CollectionViewSource.GetDefaultView(_switches);
        _switchView.Filter = FilterSwitch;
        ListSwitches.ItemsSource = _switchView;

        _varView = CollectionViewSource.GetDefaultView(_variables);
        _varView.Filter = FilterVariable;
        ListVariables.ItemsSource = _varView;

        if (_renderer != null)
        {
            StatusText.Text = "● 스위치·변수를 불러오는 중...";
            _renderer.DataInspectorUpdated += OnDataInspectorUpdated;
            _pollTimer.Tick += (_, _) => _renderer.RequestDataInspector();
            _pollTimer.Start();
            _renderer.RequestDataInspector();
        }
        else
        {
            StatusText.Text = "● 오프라인 (게임 실행 중이 아님)";
        }
    }

    private bool FilterSwitch(object obj)
    {
        if (obj is not SwitchItem sw) return false;
        string query = SearchSwitchBox.Text.Trim();
        if (string.IsNullOrEmpty(query)) return true;

        if (sw.Id.ToString() == query) return true;
        return sw.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private bool FilterVariable(object obj)
    {
        if (obj is not VariableItem va) return false;
        string query = SearchVarBox.Text.Trim();
        if (string.IsNullOrEmpty(query)) return true;

        if (va.Id.ToString() == query) return true;
        return va.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void OnDataInspectorUpdated(List<SwitchItem> switches, List<VariableItem> variables)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateSwitches(switches);
            UpdateVariables(variables);
            StatusText.Text = $"● 실시간 동기화 중 (스위치: {_switches.Count}개, 변수: {_variables.Count}개)";
        });
    }

    // 항목은 INotifyPropertyChanged라 값만 바꾸면 보이는 줄만 다시 그려집니다. 예전에는 0.5초마다 목록 전체를
    // Refresh(모든 줄 다시 생성)하고 처음 채울 때 수천 번 Add해서, 변수가 많은 게임에서 창이 크게 버벅였습니다.
    private void UpdateSwitches(List<SwitchItem> newSwitches)
    {
        if (_switches.Count == newSwitches.Count)
        {
            for (int i = 0; i < newSwitches.Count; i++)
            {
                var cur = _switches[i];
                var n = newSwitches[i];
                if (ReferenceEquals(cur, n)) continue;
                if (cur.Name != n.Name) cur.Name = n.Name;
                if (cur.Value != n.Value) cur.Value = n.Value;
                if (cur.IsFrozen != n.IsFrozen) cur.IsFrozen = n.IsFrozen;
            }
            return;
        }
        _switches = new ObservableCollection<SwitchItem>(newSwitches);
        _switchView = CollectionViewSource.GetDefaultView(_switches);
        _switchView.Filter = FilterSwitch;
        ListSwitches.ItemsSource = _switchView;
    }

    private void UpdateVariables(List<VariableItem> newVariables)
    {
        var pinnedIds = new HashSet<int>(_variables.Where(v => v.IsPinned).Select(v => v.Id));
        if (_variables.Count == newVariables.Count)
        {
            bool pinnedValueChanged = false;
            for (int i = 0; i < newVariables.Count; i++)
            {
                var cur = _variables[i];
                var n = newVariables[i];
                if (!ReferenceEquals(cur, n))
                {
                    if (cur.Name != n.Name) cur.Name = n.Name;
                    if (cur.Value != n.Value) { cur.Value = n.Value; pinnedValueChanged |= cur.IsPinned; }
                    if (cur.IsFrozen != n.IsFrozen) cur.IsFrozen = n.IsFrozen;
                }
                else if (cur.IsPinned) pinnedValueChanged = true;
            }
            if (pinnedValueChanged) NotifyPinnedChanged();
            return;
        }
        foreach (var v in newVariables)
            if (pinnedIds.Contains(v.Id)) v.IsPinned = true;
        _variables = new ObservableCollection<VariableItem>(newVariables);
        _varView = CollectionViewSource.GetDefaultView(_variables);
        _varView.Filter = FilterVariable;
        ListVariables.ItemsSource = _varView;
        if (pinnedIds.Count > 0) NotifyPinnedChanged();
    }

    private void SearchSwitchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _switchView?.Refresh();
    }

    private void BtnClearSwitchSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchSwitchBox.Text = "";
    }

    private void SearchVarBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _varView?.Refresh();
    }

    private void BtnClearVarSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchVarBox.Text = "";
    }

    private void SwitchFreeze_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is SwitchItem sw)
        {
            bool isFrozen = cb.IsChecked == true;
            sw.IsFrozen = isFrozen;
            _renderer?.FreezeSwitch(sw.Id, isFrozen, sw.Value);
        }
    }

    private void SwitchToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is SwitchItem sw)
        {
            bool newVal = !sw.Value;
            sw.Value = newVal;
            _renderer?.SetSwitch(sw.Id, newVal);
            if (sw.IsFrozen)
            {
                _renderer?.FreezeSwitch(sw.Id, true, newVal);
            }
        }
    }

    public event Action<List<VariableItem>>? PinnedVariablesChanged;

    private void VariableFreeze_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is VariableItem va)
        {
            bool isFrozen = cb.IsChecked == true;
            va.IsFrozen = isFrozen;
            _renderer?.FreezeVariable(va.Id, isFrozen, va.Value);
        }
    }

    private void VariablePin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is VariableItem va)
        {
            va.IsPinned = cb.IsChecked == true;
            NotifyPinnedChanged();
        }
    }

    private void NotifyPinnedChanged()
    {
        var pinned = _variables.Where(v => v.IsPinned).ToList();
        PinnedVariablesChanged?.Invoke(pinned);
    }

    private void VariableApply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is VariableItem va)
        {
            // 부모 StackPanel에서 TxtNewVal 찾기
            if (btn.Parent is StackPanel panel)
            {
                var txt = panel.Children.OfType<TextBox>().FirstOrDefault();
                if (txt != null && !string.IsNullOrWhiteSpace(txt.Text))
                {
                    string newVal = txt.Text.Trim();
                    va.Value = newVal;
                    _renderer?.SetVariable(va.Id, newVal);
                    if (va.IsFrozen)
                    {
                        _renderer?.FreezeVariable(va.Id, true, newVal);
                    }
                    txt.Text = "";
                }
            }
        }
    }

    private void TxtNewVal_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox txt && txt.DataContext is VariableItem va)
        {
            if (!string.IsNullOrWhiteSpace(txt.Text))
            {
                string newVal = txt.Text.Trim();
                va.Value = newVal;
                _renderer?.SetVariable(va.Id, newVal);
                if (va.IsFrozen)
                {
                    _renderer?.FreezeVariable(va.Id, true, newVal);
                }
                txt.Text = "";
            }
            e.Handled = true;
        }
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        _renderer?.RequestDataInspector();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _pollTimer.Stop();
        if (_renderer != null)
        {
            _renderer.DataInspectorUpdated -= OnDataInspectorUpdated;
        }
        base.OnClosed(e);
    }
}
