using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace RPM.App.Converters;

public class BoolToSharingTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? "Đang chia sẻ" : "Chưa chia sẻ";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToColorTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? "Có màu" : "Đơn sắc";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToDuplexTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? "In 2 mặt" : "1 mặt";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToDefaultTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? "★ Mặc định" : "Phụ";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToApprovedTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? "Đã duyệt" : "Chờ duyệt";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToOnlineTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? "● Trực tuyến" : "○ Ngoại tuyến";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToBadgeBgConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Light green #E8F8EE vs light cream-gray #F4ECE5
        return value is true 
            ? new SolidColorBrush(Color.FromArgb(255, 232, 248, 238)) 
            : new SolidColorBrush(Color.FromArgb(255, 244, 236, 229));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToBadgeFgConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Success green #15B84A vs text muted #64748B
        return value is true 
            ? new SolidColorBrush(Color.FromArgb(255, 21, 184, 74)) 
            : new SolidColorBrush(Color.FromArgb(255, 100, 116, 139));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class OnlineBadgeBgConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true 
            ? new SolidColorBrush(Color.FromArgb(255, 232, 248, 238)) 
            : new SolidColorBrush(Color.FromArgb(255, 250, 236, 235));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class OnlineBadgeFgConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true 
            ? new SolidColorBrush(Color.FromArgb(255, 21, 184, 74)) 
            : new SolidColorBrush(Color.FromArgb(255, 197, 61, 50));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class LevelToBadgeBgConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is RPM.Core.Diagnostics.CompatibilityLevel level)
        {
            return level switch
            {
                RPM.Core.Diagnostics.CompatibilityLevel.Compatible => new SolidColorBrush(Color.FromArgb(255, 232, 248, 238)),
                RPM.Core.Diagnostics.CompatibilityLevel.NeedsSetup => new SolidColorBrush(Color.FromArgb(255, 254, 246, 230)),
                RPM.Core.Diagnostics.CompatibilityLevel.Incompatible => new SolidColorBrush(Color.FromArgb(255, 250, 236, 235)),
                _ => new SolidColorBrush(Color.FromArgb(255, 244, 236, 229))
            };
        }
        return new SolidColorBrush(Color.FromArgb(255, 244, 236, 229));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class LevelToBadgeFgConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is RPM.Core.Diagnostics.CompatibilityLevel level)
        {
            return level switch
            {
                RPM.Core.Diagnostics.CompatibilityLevel.Compatible => new SolidColorBrush(Color.FromArgb(255, 21, 184, 74)),
                RPM.Core.Diagnostics.CompatibilityLevel.NeedsSetup => new SolidColorBrush(Color.FromArgb(255, 242, 169, 0)),
                RPM.Core.Diagnostics.CompatibilityLevel.Incompatible => new SolidColorBrush(Color.FromArgb(255, 197, 61, 50)),
                _ => new SolidColorBrush(Color.FromArgb(255, 100, 116, 139))
            };
        }
        return new SolidColorBrush(Color.FromArgb(255, 100, 116, 139));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class LevelToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is RPM.Core.Diagnostics.CompatibilityLevel level)
        {
            return level switch
            {
                RPM.Core.Diagnostics.CompatibilityLevel.Compatible => "● Tương thích",
                RPM.Core.Diagnostics.CompatibilityLevel.NeedsSetup => "▲ Cần thiết lập",
                RPM.Core.Diagnostics.CompatibilityLevel.Incompatible => "✕ Không tương thích",
                _ => "Chưa xác định"
            };
        }
        return "Chưa xác định";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

