using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace AppManager
{
    public enum CategoryIconKind
    {
        Star,
        Quick,
        Code,
        Settings,
        Briefcase,
        Globe,
        Chat,
        Media,
        Design,
        Network,
        Grid,
        Photo,
        Video,
        Music,
        Document,
        IdCard,
        Archive,
        Folder,
        Files
    }

    internal static class CategoryIcons
    {
        public static readonly CategoryIconKind[] PickerKinds =
        {
            CategoryIconKind.Folder, CategoryIconKind.Star, CategoryIconKind.Code,
            CategoryIconKind.Settings, CategoryIconKind.Briefcase, CategoryIconKind.Globe,
            CategoryIconKind.Chat, CategoryIconKind.Media, CategoryIconKind.Design,
            CategoryIconKind.Network, CategoryIconKind.Grid, CategoryIconKind.Photo,
            CategoryIconKind.Video, CategoryIconKind.Music, CategoryIconKind.Document,
            CategoryIconKind.IdCard, CategoryIconKind.Archive, CategoryIconKind.Files
        };

        public static CategoryIconKind ForCategory(string name)
        {
            switch (name)
            {
                case "常用": return CategoryIconKind.Star;
                case "快捷中心": return CategoryIconKind.Quick;
                case "开发工具": return CategoryIconKind.Code;
                case "系统工具": return CategoryIconKind.Settings;
                case "办公软件": return CategoryIconKind.Briefcase;
                case "浏览器": return CategoryIconKind.Globe;
                case "通讯社交": return CategoryIconKind.Chat;
                case "媒体娱乐": return CategoryIconKind.Media;
                case "图像设计": return CategoryIconKind.Design;
                case "网络工具": return CategoryIconKind.Network;
                case "照片": return CategoryIconKind.Photo;
                case "视频": return CategoryIconKind.Video;
                case "音乐": return CategoryIconKind.Music;
                case "文档": return CategoryIconKind.Document;
                case "证件": return CategoryIconKind.IdCard;
                case "压缩包": return CategoryIconKind.Archive;
                case "文件": return CategoryIconKind.Folder;
                case "全部文件": return CategoryIconKind.Files;
                case "全部软件":
                case "全部资源": return CategoryIconKind.Grid;
                default: return CategoryIconKind.Folder;
            }
        }

        public static CategoryIconKind Parse(string value, CategoryIconKind fallback)
        {
            CategoryIconKind kind;
            return Enum.TryParse(value, true, out kind) ? kind : fallback;
        }

        public static string ConfigKey(bool resource, string name)
        {
            return (resource ? "resource:" : "software:") + (name ?? string.Empty);
        }

        public static string DisplayName(CategoryIconKind kind)
        {
            switch (kind)
            {
                case CategoryIconKind.Star: return "星标";
                case CategoryIconKind.Quick: return "快捷";
                case CategoryIconKind.Code: return "代码";
                case CategoryIconKind.Settings: return "设置";
                case CategoryIconKind.Briefcase: return "办公";
                case CategoryIconKind.Globe: return "全球";
                case CategoryIconKind.Chat: return "对话";
                case CategoryIconKind.Media: return "媒体";
                case CategoryIconKind.Design: return "设计";
                case CategoryIconKind.Network: return "网络";
                case CategoryIconKind.Grid: return "应用";
                case CategoryIconKind.Photo: return "照片";
                case CategoryIconKind.Video: return "视频";
                case CategoryIconKind.Music: return "音乐";
                case CategoryIconKind.Document: return "文档";
                case CategoryIconKind.IdCard: return "证件";
                case CategoryIconKind.Archive: return "归档";
                case CategoryIconKind.Files: return "文件";
                default: return "文件夹";
            }
        }

        public static void DrawIcon(Graphics graphics, CategoryIconKind kind, Rectangle bounds, Color color)
        {
            GraphicsState state = graphics.Save();
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float centerX = bounds.X + bounds.Width / 2F;
            float centerY = bounds.Y + bounds.Height / 2F;
            float scale = Math.Min(bounds.Width, bounds.Height) / 20F;
            graphics.TranslateTransform(centerX, centerY);
            graphics.ScaleTransform(scale, scale);
            using (Pen pen = new Pen(color, 1.8F))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                switch (kind)
                {
                    case CategoryIconKind.Star:
                        PointF[] star = new PointF[10];
                        for (int index = 0; index < star.Length; index++)
                        {
                            double angle = -Math.PI / 2 + index * Math.PI / 5;
                            float radius = index % 2 == 0 ? 8F : 3.8F;
                            star[index] = new PointF((float)(Math.Cos(angle) * radius),
                                (float)(Math.Sin(angle) * radius));
                        }
                        graphics.DrawPolygon(pen, star);
                        break;
                    case CategoryIconKind.Quick:
                        graphics.DrawLines(pen, new[]
                        {
                            new PointF(2F, -9F), new PointF(-5F, 1F), new PointF(0F, 1F),
                            new PointF(-2F, 9F), new PointF(6F, -2F), new PointF(1F, -2F),
                            new PointF(2F, -9F)
                        });
                        break;
                    case CategoryIconKind.Code:
                        graphics.DrawLines(pen, new[] { new PointF(-2F, -6F), new PointF(-8F, 0F), new PointF(-2F, 6F) });
                        graphics.DrawLines(pen, new[] { new PointF(2F, -6F), new PointF(8F, 0F), new PointF(2F, 6F) });
                        graphics.DrawLine(pen, 1F, -8F, -1F, 8F);
                        break;
                    case CategoryIconKind.Settings:
                        graphics.DrawEllipse(pen, -6.5F, -6.5F, 13F, 13F);
                        graphics.DrawEllipse(pen, -2F, -2F, 4F, 4F);
                        graphics.DrawLine(pen, 0F, -9F, 0F, -6.5F);
                        graphics.DrawLine(pen, 0F, 6.5F, 0F, 9F);
                        graphics.DrawLine(pen, -9F, 0F, -6.5F, 0F);
                        graphics.DrawLine(pen, 6.5F, 0F, 9F, 0F);
                        break;
                    case CategoryIconKind.Briefcase:
                        graphics.DrawRectangle(pen, -8F, -4F, 16F, 11F);
                        graphics.DrawRectangle(pen, -3F, -7F, 6F, 3F);
                        graphics.DrawLine(pen, -8F, 0F, 8F, 0F);
                        graphics.DrawLine(pen, 0F, -1F, 0F, 2F);
                        break;
                    case CategoryIconKind.Globe:
                        graphics.DrawEllipse(pen, -8F, -8F, 16F, 16F);
                        graphics.DrawEllipse(pen, -3.5F, -8F, 7F, 16F);
                        graphics.DrawLine(pen, -8F, 0F, 8F, 0F);
                        graphics.DrawArc(pen, -6F, -5F, 12F, 10F, 180F, 180F);
                        graphics.DrawArc(pen, -6F, -5F, 12F, 10F, 0F, 180F);
                        break;
                    case CategoryIconKind.Chat:
                        graphics.DrawRoundedRectangle(pen, -8F, -7F, 16F, 12F, 3F);
                        graphics.DrawLines(pen, new[] { new PointF(-4F, 5F), new PointF(-5F, 9F), new PointF(1F, 5F) });
                        break;
                    case CategoryIconKind.Media:
                        graphics.DrawEllipse(pen, -8F, -8F, 16F, 16F);
                        graphics.DrawLines(pen, new[] { new PointF(-2F, -4F), new PointF(4F, 0F), new PointF(-2F, 4F), new PointF(-2F, -4F) });
                        break;
                    case CategoryIconKind.Design:
                        graphics.DrawLines(pen, new[]
                        {
                            new PointF(-7F, 6F), new PointF(-5F, 0F), new PointF(5F, -8F),
                            new PointF(8F, -5F), new PointF(0F, 5F), new PointF(-7F, 6F)
                        });
                        graphics.DrawLine(pen, -5F, 0F, 0F, 5F);
                        break;
                    case CategoryIconKind.Network:
                        graphics.DrawLine(pen, 0F, -4F, -5F, 4F);
                        graphics.DrawLine(pen, 0F, -4F, 5F, 4F);
                        graphics.DrawLine(pen, -5F, 4F, 5F, 4F);
                        graphics.DrawEllipse(pen, -3F, -9F, 6F, 6F);
                        graphics.DrawEllipse(pen, -8F, 2F, 6F, 6F);
                        graphics.DrawEllipse(pen, 2F, 2F, 6F, 6F);
                        break;
                    case CategoryIconKind.Grid:
                        DrawGrid(graphics, pen);
                        break;
                    case CategoryIconKind.Photo:
                        graphics.DrawRectangle(pen, -8F, -7F, 16F, 14F);
                        graphics.DrawEllipse(pen, 2F, -4F, 3F, 3F);
                        graphics.DrawLines(pen, new[] { new PointF(-7F, 5F), new PointF(-2F, 0F), new PointF(2F, 4F), new PointF(5F, 1F), new PointF(8F, 5F) });
                        break;
                    case CategoryIconKind.Video:
                        graphics.DrawRoundedRectangle(pen, -8F, -7F, 16F, 14F, 2F);
                        graphics.DrawLines(pen, new[] { new PointF(-2F, -3F), new PointF(4F, 0F), new PointF(-2F, 3F), new PointF(-2F, -3F) });
                        break;
                    case CategoryIconKind.Music:
                        graphics.DrawLine(pen, 2F, 3F, 2F, -7F);
                        graphics.DrawLine(pen, 2F, -7F, 7F, -5F);
                        graphics.DrawLine(pen, 7F, -5F, 7F, 5F);
                        graphics.DrawEllipse(pen, -4F, 1F, 6F, 5F);
                        graphics.DrawEllipse(pen, 1F, 3F, 6F, 5F);
                        break;
                    case CategoryIconKind.Document:
                        graphics.DrawLines(pen, new[]
                        {
                            new PointF(-6F, -8F), new PointF(2F, -8F), new PointF(7F, -3F),
                            new PointF(7F, 8F), new PointF(-6F, 8F), new PointF(-6F, -8F)
                        });
                        graphics.DrawLines(pen, new[] { new PointF(2F, -8F), new PointF(2F, -3F), new PointF(7F, -3F) });
                        graphics.DrawLine(pen, -3F, 1F, 4F, 1F);
                        graphics.DrawLine(pen, -3F, 4F, 4F, 4F);
                        break;
                    case CategoryIconKind.IdCard:
                        graphics.DrawRoundedRectangle(pen, -9F, -6F, 18F, 12F, 2F);
                        graphics.DrawEllipse(pen, -6F, -3F, 5F, 5F);
                        graphics.DrawLine(pen, 2F, -2F, 6F, -2F);
                        graphics.DrawLine(pen, 2F, 2F, 6F, 2F);
                        graphics.DrawArc(pen, -7F, 1F, 7F, 5F, 180F, 180F);
                        break;
                    case CategoryIconKind.Archive:
                        graphics.DrawRectangle(pen, -7F, -5F, 14F, 12F);
                        graphics.DrawRectangle(pen, -8F, -8F, 16F, 4F);
                        graphics.DrawLine(pen, -2F, -1F, 2F, -1F);
                        break;
                    case CategoryIconKind.Files:
                        graphics.DrawRectangle(pen, -6F, -8F, 11F, 14F);
                        graphics.DrawRectangle(pen, -3F, -5F, 10F, 13F);
                        break;
                    default:
                        graphics.DrawLines(pen, new[]
                        {
                            new PointF(-8F, -5F), new PointF(-2F, -5F), new PointF(0F, -7F),
                            new PointF(7F, -7F), new PointF(8F, 7F), new PointF(-8F, 7F),
                            new PointF(-8F, -5F)
                        });
                        break;
                }
            }
            graphics.Restore(state);
        }

        private static void DrawGrid(Graphics graphics, Pen pen)
        {
            graphics.DrawRoundedRectangle(pen, -8F, -8F, 6F, 6F, 1F);
            graphics.DrawRoundedRectangle(pen, 2F, -8F, 6F, 6F, 1F);
            graphics.DrawRoundedRectangle(pen, -8F, 2F, 6F, 6F, 1F);
            graphics.DrawRoundedRectangle(pen, 2F, 2F, 6F, 6F, 1F);
        }

        private static void DrawRoundedRectangle(this Graphics graphics, Pen pen,
            float x, float y, float width, float height, float radius)
        {
            float diameter = radius * 2F;
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddArc(x, y, diameter, diameter, 180F, 90F);
                path.AddArc(x + width - diameter, y, diameter, diameter, 270F, 90F);
                path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0F, 90F);
                path.AddArc(x, y + height - diameter, diameter, diameter, 90F, 90F);
                path.CloseFigure();
                graphics.DrawPath(pen, path);
            }
        }
    }
}
