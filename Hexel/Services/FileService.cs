using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using Hexel.Core;

namespace Hexel.Services
{
    public class FileService : IFileService
    {
        private const string Filter = "Hexel Sprite (*.hexel)|*.hexel|JSON Files (*.json)|*.json|All Files (*.*)|*.*";

        public void SaveSprite(SpriteState state)
        {
            var dialog = new SaveFileDialog
            {
                Filter = Filter,
                DefaultExt = ".hexel",
                Title = "Save Sprite"
            };

            if (dialog.ShowDialog() == true)
            {
                // Serialize the SpriteState object to a JSON string
                string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(dialog.FileName, json);
            }
        }

        public SpriteState LoadSprite()
        {
            var dialog = new OpenFileDialog
            {
                Filter = Filter,
                Title = "Load Sprite"
            };

            if (dialog.ShowDialog() == true)
            {
                // Read the JSON string and deserialize it back into a SpriteState object
                string json = File.ReadAllText(dialog.FileName);
                return JsonSerializer.Deserialize<SpriteState>(json);
            }

            return null;
        }
    }
}