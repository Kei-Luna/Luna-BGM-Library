using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LunaBgmLibrary
{
    public class AllFoldersForegroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return new SolidColorBrush(Colors.Black);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}