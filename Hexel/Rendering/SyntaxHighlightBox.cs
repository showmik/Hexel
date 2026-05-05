using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Hexel.Rendering
{
    public enum TokenType
    {
        Default,
        Keyword,
        Literal,
        Comment,
        Identifier
    }

    public readonly struct TokenSpan
    {
        public int Start { get; }
        public int Length { get; }
        public TokenType Type { get; }

        public TokenSpan(int start, int length, TokenType type)
        {
            Start = start;
            Length = length;
            Type = type;
        }
    }

    public class SyntaxHighlightBox : FrameworkElement
    {
        private FormattedText? _formattedText;

        public static readonly DependencyProperty FontFamilyProperty =
            DependencyProperty.Register(nameof(FontFamily), typeof(FontFamily), typeof(SyntaxHighlightBox),
                new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public FontFamily FontFamily
        {
            get => (FontFamily)GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        public static readonly DependencyProperty FontSizeProperty =
            DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(SyntaxHighlightBox),
                new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public double FontSize
        {
            get => (double)GetValue(FontSizeProperty);
            set => SetValue(FontSizeProperty, value);
        }

        public void UpdateCode(string text, List<TokenSpan> spans, 
            SolidColorBrush defaultBrush, SolidColorBrush keywordBrush, 
            SolidColorBrush literalBrush, SolidColorBrush commentBrush, 
            SolidColorBrush identifierBrush)
        {
            if (string.IsNullOrEmpty(text))
            {
                _formattedText = null;
                InvalidateMeasure();
                InvalidateVisual();
                return;
            }

            // Create Typeface
            var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            // Create FormattedText with default brush
            _formattedText = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                FontSize,
                defaultBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            // Apply color spans
            foreach (var span in spans)
            {
                SolidColorBrush brush = span.Type switch
                {
                    TokenType.Keyword => keywordBrush,
                    TokenType.Literal => literalBrush,
                    TokenType.Comment => commentBrush,
                    TokenType.Identifier => identifierBrush,
                    _ => defaultBrush
                };

                _formattedText.SetForegroundBrush(brush, span.Start, span.Length);
            }

            InvalidateMeasure();
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (_formattedText == null)
                return new Size(0, 0);

            return new Size(_formattedText.WidthIncludingTrailingWhitespace, _formattedText.Height);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            if (_formattedText != null)
            {
                drawingContext.DrawText(_formattedText, new Point(0, 0));
            }
        }
    }
}
