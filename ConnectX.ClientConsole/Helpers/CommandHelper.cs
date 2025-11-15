using System.CommandLine;

namespace ConnectX.ClientConsole.Helpers;

public static class CommandHelper
{
    extension<T>(Option<T> option)
    {
        public Option<T> Required()
        {
            option.Required = true;
            return option;
        }

        public Option<T> WithDefault(T value)
        {
            option.DefaultValueFactory = _ => value;
            return option;
        }
    }
}