using Hexel.Core;

namespace Hexel.Services
{
    public interface IFileService
    {
        void SaveSprite(SpriteState state);
        SpriteState LoadSprite();
    }
}