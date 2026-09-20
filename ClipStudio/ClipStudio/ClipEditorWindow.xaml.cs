using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Globalization;
using ClipStudio.ViewModels;

namespace ClipStudio
{
    public class EnumToBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value?.Equals(parameter) ?? false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isChecked && isChecked)
            {
                return parameter;
            }
            return Binding.DoNothing;
        }
    }

    public partial class ClipEditorWindow : Window
    {
        public ClipEditorWindow()
        {
            InitializeComponent();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is ClipEditorViewModel vm)
            {
                vm.CloseRequested += (result) =>
                {
                    DialogResult = result;
                };

                await vm.LoadAsync();
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (DataContext is ClipEditorViewModel vm && vm.IsLoading)
            {
                vm.CancelLoading();
            }
        }
    }
}
