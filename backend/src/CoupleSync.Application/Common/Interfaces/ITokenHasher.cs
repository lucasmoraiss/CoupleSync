namespace CoupleSync.Application.Common.Interfaces;

public interface ITokenHasher
{
    string Hash(string value);
}
