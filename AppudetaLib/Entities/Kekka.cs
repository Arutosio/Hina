using AppudetaLib.Entities.Tree;

namespace AppudetaLib.Entities
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