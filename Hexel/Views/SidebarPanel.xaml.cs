using Hexel.Core;
using Hexel.ViewModels;
using System;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Hexel.Views
{
    public partial class SidebarPanel : UserControl
    {
        // ── Syntax colours (picked up from theme resources at runtime) ──────
        private SolidColorBrush _keywordBrush = new(Color.FromRgb(0x56, 0x9C, 0xD6));
        private SolidColorBrush _literalBrush = new(Color.FromRgb(0xB5, 0xCE, 0xA8));
        private SolidColorBrush _commentBrush = new(Color.FromRgb(0x6A, 0x99, 0x55));
        private SolidColorBrush _identifierBrush = new(Color.FromRgb(0x9C, 0xDC, 0xFE));
        private SolidColorBrush _defaultBrush = new(Color.FromRgb(0xD4, 0xD4, 0xD4));

        // Compiled once — avoids re-parsing the regex pattern on every export update
        private static readonly Regex s_tokenRegex = new(
            @"(0[xX][0-9a-fA-F]+|\d+)" +              // hex OR decimal literal
            @"|(const|uint8_t|PROGMEM|U8X8_PROGMEM|bytearray|framebuf|display|u8g2)" + // keywords
            @"|([a-zA-Z_][a-zA-Z0-9_]*)" +            // identifiers
            @"|([^a-zA-Z0-9_]+)",                      // punctuation / whitespace
            RegexOptions.Compiled);

        private MainViewModel? _vm;

        public SidebarPanel()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_vm != null)
                _vm.PropertyChanged -= Vm_PropertyChanged;

            _vm = e.NewValue as MainViewModel;

            if (_vm != null)
            {
                _vm.PropertyChanged += Vm_PropertyChanged;
                SyncChipsFromViewModel();
                UpdateSyntaxOutput(_vm.ExportedCode);
            }
        }

        private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.ExportedCode))
            {
                Dispatcher.BeginInvoke(() => UpdateSyntaxOutput(_vm?.ExportedCode ?? string.Empty));
            }
            else if (e.PropertyName == nameof(MainViewModel.ExportFormat))
            {
                Dispatcher.BeginInvoke(SyncFormatCombo);
            }
            else if (e.PropertyName == nameof(MainViewModel.BytesPerLine))
            {
                Dispatcher.BeginInvoke(SyncBplChip);
            }
            else if (e.PropertyName == nameof(MainViewModel.UppercaseHex))
            {
                Dispatcher.BeginInvoke(SyncHexCaseChip);
            }
        }

        // ── Format ComboBox ──────────────────────────────────────────────────

        private bool _suppressFormatChange;

        private void CboFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressFormatChange || _vm == null) return;
            if (CboFormat.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                if (Enum.TryParse<ExportFormat>(tag, out var fmt))
                    _vm.ExportFormat = fmt;
            }
        }

        private void SyncFormatCombo()
        {
            if (_vm == null) return;
            _suppressFormatChange = true;
            string target = _vm.ExportFormat.ToString();
            foreach (ComboBoxItem item in CboFormat.Items)
            {
                if (item.Tag is string t && t == target)
                {
                    CboFormat.SelectedItem = item;
                    break;
                }
            }
            _suppressFormatChange = false;
        }

        // ── Bytes-per-line chips ─────────────────────────────────────────────

        private bool _suppressBplChange;

        private void BplChip_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressBplChange || _vm == null) return;
            if (sender is RadioButton rb && rb.Tag is string t && int.TryParse(t, out int v))
                _vm.BytesPerLine = v;
        }

        private void SyncBplChip()
        {
            if (_vm == null) return;
            _suppressBplChange = true;
            string tag = _vm.BytesPerLine.ToString();
            foreach (var rb in new[] { RbBpl0, RbBpl4, RbBpl8, RbBpl16 })
            {
                if (rb.Tag is string t && t == tag)
                { rb.IsChecked = true; break; }
            }
            _suppressBplChange = false;
        }

        // ── Hex-case chips ───────────────────────────────────────────────────

        private bool _suppressHexCaseChange;

        private void HexCase_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressHexCaseChange || _vm == null) return;
            _vm.UppercaseHex = (sender is RadioButton rb && rb.Tag is string t && t == "upper");
        }

        private void SyncHexCaseChip()
        {
            if (_vm == null) return;
            _suppressHexCaseChange = true;
            RbHexUpper.IsChecked = _vm.UppercaseHex;
            RbHexLower.IsChecked = !_vm.UppercaseHex;
            _suppressHexCaseChange = false;
        }

        // ── Master sync ──────────────────────────────────────────────────────

        private void SyncChipsFromViewModel()
        {
            if (_vm == null) return;
            SyncFormatCombo();
            SyncBplChip();
            SyncHexCaseChip();
        }

        // ── Syntax highlighting ──────────────────────────────────────────────

        private void UpdateSyntaxOutput(string code)
        {
            // Resolve brushes from theme resources (so they respect theme switches)
            TryLoadBrushes();

            var doc = new FlowDocument
            {
                FontFamily = RtbOutput.FontFamily,
                FontSize = RtbOutput.FontSize,
                PagePadding = new Thickness(0),
            };

            if (string.IsNullOrEmpty(code))
            {
                doc.Blocks.Add(new Paragraph(new Run("(no output)") { Foreground = _commentBrush }));
                RtbOutput.Document = doc;
                return;
            }

            string[] lines = code.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (string line in lines)
            {
                var para = new Paragraph { Margin = new Thickness(0), LineHeight = 18 };
                AppendColouredLine(para.Inlines, line);
                doc.Blocks.Add(para);
            }

            RtbOutput.Document = doc;
        }

        private void AppendColouredLine(InlineCollection inlines, string line)
        {
            // Tokenise the line with a simple regex scanner:
            // 1. Comments: // … to end of line
            // 2. Hex literals: 0x / 0X followed by hex digits
            // 3. C keywords / type names
            // 4. Python keywords
            // 5. Everything else

            int pos = 0;

            // Comment check: if the trimmed line starts with //, whole line is a comment
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("//") || trimmed.StartsWith("#"))
            {
                inlines.Add(new Run(line) { Foreground = _commentBrush });
                return;
            }

            // Split at embedded comment (e.g. "  0xFF,  // row 0")
            int commentIdx = FindInlineComment(line);
            string codePart = commentIdx >= 0 ? line[..commentIdx] : line;
            string commentPart = commentIdx >= 0 ? line[commentIdx..] : string.Empty;

            foreach (Match m in s_tokenRegex.Matches(codePart))
            {
                string token = m.Value;
                SolidColorBrush brush;

                if (m.Groups[1].Success)
                    brush = _literalBrush;   // hex literal
                else if (m.Groups[2].Success)
                    brush = _keywordBrush;   // keyword / type
                else if (m.Groups[3].Success)
                    brush = _identifierBrush; // identifier
                else
                    brush = _defaultBrush;   // punctuation

                inlines.Add(new Run(token) { Foreground = brush });
                pos += token.Length;
            }

            // Append trailing comment part
            if (!string.IsNullOrEmpty(commentPart))
                inlines.Add(new Run(commentPart) { Foreground = _commentBrush });
        }

        /// <summary>
        /// Returns the index of the first '//' that isn't inside a string literal.
        /// Returns -1 if there is no inline comment.
        /// </summary>
        private static int FindInlineComment(string line)
        {
            bool inString = false;
            for (int i = 0; i < line.Length - 1; i++)
            {
                if (line[i] == '"') inString = !inString;
                if (!inString && line[i] == '/' && line[i + 1] == '/')
                    return i;
            }
            return -1;
        }

        private void TryLoadBrushes()
        {
            try
            {
                _keywordBrush = (SolidColorBrush)FindResource("Syntax.KeywordBrush");
                _literalBrush = (SolidColorBrush)FindResource("Syntax.LiteralBrush");
                _commentBrush = (SolidColorBrush)FindResource("Syntax.CommentBrush");
                _identifierBrush = (SolidColorBrush)FindResource("Syntax.IdentifierBrush");
                _defaultBrush = (SolidColorBrush)FindResource("Syntax.PunctuationBrush");
            }
            catch { /* fallback colours already set in field initialisers */ }
        }

        // ── Import button ────────────────────────────────────────────────────

        private void ImportFromCode_Click(object sender, RoutedEventArgs e)
        {
            // Reach up to the ShellViewModel (MainWindow's DataContext) and invoke its command
            if (Application.Current.MainWindow?.DataContext is ShellViewModel shell &&
                shell.ImportFromCodeMenuCommand.CanExecute(null))
            {
                shell.ImportFromCodeMenuCommand.Execute(null);
            }
        }
    }
}
