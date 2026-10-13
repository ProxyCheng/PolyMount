namespace PolyMount.Core;

public interface IRevisionSource : ILayer
{
    long Revision { get; }
    long HeadRevision();
    void SetRevision(long rev);
}
