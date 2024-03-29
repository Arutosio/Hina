using HinaLib.Entities.Tree;

namespace HinaLib.Entities
{
    public class Kekka
    {
        public Status Status { get; set; }
        public string Message { get; set; }

        public Kekka()
        {
            Status = Status.Unknow;
            Message = string.Empty;
        }

        public Kekka(Status status, string message)
        {
            Status = status;
            Message = message;
        }
    }

}