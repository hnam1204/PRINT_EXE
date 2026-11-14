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
        // Light green vs light gray
        return value is true 
            ? new SolidColorBrush(Color.FromArgb(255, 222, 247, 236)) 
            : new SolidColorBrush(Color.FromArgb(255, 243, 244, 246));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BoolToBadgeFgConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Dark green vs dark gray
        return value is true 
            ? new SolidColorBrush(Color.FromArgb(255, 3, 84, 63)) 
            : new SolidColorBrush(Color.FromArgb(255, 107, 114, 128));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class OnlineBadgeBgConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true 
            ? new SolidColorBrush(Color.FromArgb(255, 222, 247, 236)) 
            : new SolidColorBrush(Color.FromArgb(255, 254, 242, 242));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class OnlineBadgeFgConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true 
            ? new SolidColorBrush(Color.FromArgb(255, 3, 84, 63)) 
            : new SolidColorBrush(Color.FromArgb(255, 155, 28, 28));
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
                RPM.Core.Diagnostics.CompatibilityLevel.Compatible => new SolidColorBrush(Color.FromArgb(255, 222, 247, 236)),
                RPM.Core.Diagnostics.CompatibilityLevel.NeedsSetup => new SolidColorBrush(Color.FromArgb(255, 254, 243, 199)),
                RPM.Core.Diagnostics.CompatibilityLevel.Incompatible => new SolidColorBrush(Color.FromArgb(255, 254, 226, 226)),
                _ => new SolidColorBrush(Color.FromArgb(255, 243, 244, 246))
            };
        }
        return new SolidColorBrush(Color.FromArgb(255, 243, 244, 246));
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
                RPM.Core.Diagnostics.CompatibilityLevel.Compatible => new SolidColorBrush(Color.FromArgb(255, 3, 84, 63)),
                RPM.Core.Diagnostics.CompatibilityLevel.NeedsSetup => new SolidColorBrush(Color.FromArgb(255, 180, 83, 9)),
                RPM.Core.Diagnostics.CompatibilityLevel.Incompatible => new SolidColorBrush(Color.FromArgb(255, 185, 28, 28)),
                _ => new SolidColorBrush(Color.FromArgb(255, 75, 85, 99))
            };
        }
        return new SolidColorBrush(Color.FromArgb(255, 75, 85, 99));
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

