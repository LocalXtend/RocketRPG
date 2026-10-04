#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RocketRPG.Models;

namespace RocketRPG.Views;

public partial class HotkeySettingsWindow : Window
{
    private readonly ObservableCollection<HotkeyItem> _generalItems = new();
    private readonly ObservableCollection<HotkeyItem> _cheatItems = new();
    private readonly ObservableCollection<HotkeyItem> _viewerItems = new();
    private readonly Action<Dictionary<string, string>>? _onApply;

    public HotkeySettingsWindow(Action<Dictionary<string, string>>? onApply = null)
    {
        InitializeComponent();
        _onApply = onApply;

        LoadItemsFromManager();
        ListGeneral.ItemsSource = _generalItems;
        ListCheat.ItemsSource = _cheatItems;
        ListViewer.ItemsSource = _viewerItems;

        if (_generalItems.Count > 0) ListGeneral.SelectedIndex = 0;
    }

    private void LoadItemsFromManager()
    {
        _generalItems.Clear();
        _cheatItems.Clear();
        _viewerItems.Clear();

        foreach (var h in HotkeyManager.ActiveHotkeys)
        {
            var copy = new HotkeyItem
            {
                Id = h.Id,
                Category = h.Category,
                Name = h.Name,
                Description = h.Description,
                DefaultGesture = h.DefaultGesture,
                CurrentGesture = h.CurrentGesture
            };

            if (h.Category == HotkeyManager.CatGeneral) _generalItems.Add(copy);
            else if (h.Category == HotkeyManager.CatCheat) _cheatItems.Add(copy);
            else _viewerItems.Add(copy);
        }
    }

    private IEnumerable<HotkeyItem> AllItems =>
        _generalItems.Concat(_cheatItems).Concat(_viewerItems);

    private HotkeyItem? SelectedItem
    {
        get
        {
            var tab = CategoryTabs.SelectedItem as TabItem;
            string? cat = tab?.Tag as string;
            if (cat == HotkeyManager.CatGeneral) return ListGeneral.SelectedItem as HotkeyItem;
            if (cat == HotkeyManager.CatCheat) return ListCheat.SelectedItem as HotkeyItem;
            return ListViewer.SelectedItem as HotkeyItem;
        }
    }

    private void HotkeyListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var item = SelectedItem;
        if (item != null)
        {
            KeyInputBox.Text = item.CurrentGesture;
        }
        else
        {
            KeyInputBox.Text = "";
        }
    }

    private void CategoryTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is TabControl)
        {
            HotkeyListView_SelectionChanged(sender, e);
        }
    }

    private void KeyInputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin)
        {
            return;
        }

        // 백스페이스/Delete 단독 누름 시 해제 처리
        if (key == Key.Back || key == Key.Delete)
        {
            KeyInputBox.Text = "";
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        string gesture = HotkeyManager.GestureToString(modifiers, key);
        KeyInputBox.Text = gesture;
    }

    private void BtnAssign_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedItem;
        if (item == null)
        {
            MessageBox.Show(this, "먼저 목록에서 변경할 기능을 선택해 주세요.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string newGesture = KeyInputBox.Text.Trim();
        if (string.IsNullOrEmpty(newGesture))
        {
            item.CurrentGesture = "";
            RefreshLists();
            return;
        }

        // 윈도우 조합키(Alt+Tab, Alt+F4 등)는 지정하지 않음, 메뉴 단축 글자(Alt+F 등)와 겹치면 한 번 묻기
        if (HotkeyManager.SystemConflict(newGesture) is string sys)
        {
            MessageBox.Show(this, $"'{newGesture}'은(는) 쓸 수 없습니다. {sys}", "단축키", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (HotkeyManager.MenuAccessConflict(newGesture) &&
            MessageBox.Show(this, $"'{newGesture}'은(는) 메뉴 줄을 여는 키와 겹칩니다. 그래도 지정할까요?", "단축키",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        // 충돌 검사
        var conflict = AllItems.FirstOrDefault(x => x.Id != item.Id && string.Equals(x.CurrentGesture, newGesture, StringComparison.OrdinalIgnoreCase));
        if (conflict != null)
        {
            var res = MessageBox.Show(this,
                $"'{newGesture}' 단축키는 이미 '{conflict.Name}' 기능에 할당되어 있습니다.\n기존 할당을 해제하고 이 기능에 할당하시겠습니까?",
                "단축키 충돌", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (res != MessageBoxResult.Yes) return;
            conflict.CurrentGesture = "";
        }

        item.CurrentGesture = newGesture;
        RefreshLists();
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedItem;
        if (item != null)
        {
            item.CurrentGesture = "";
            KeyInputBox.Text = "";
            RefreshLists();
        }
    }

    private void RefreshLists()
    {
        ListGeneral.Items.Refresh();
        ListCheat.Items.Refresh();
        ListViewer.Items.Refresh();
    }

    private void BtnResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "모든 단축키를 기본 설정값으로 되돌리시겠습니까?", "단축키 초기화",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            foreach (var item in AllItems)
            {
                item.CurrentGesture = item.DefaultGesture;
            }
            RefreshLists();
            var sel = SelectedItem;
            if (sel != null) KeyInputBox.Text = sel.CurrentGesture;
        }
    }

    private void SaveAndApply()
    {
        HotkeyManager.UpdateBindings(AllItems);
        var dict = HotkeyManager.SaveToDictionary();
        _onApply?.Invoke(dict);
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        SaveAndApply();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        SaveAndApply();
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
