namespace Raximod
{
    internal sealed class CommandArguments
    {
        private readonly string[] _arguments;

        public CommandArguments(IEnumerable<string> arguments)
        {
            _arguments = arguments.ToArray();
        }

        public string? Value(string name)
        {
            int index = Array.IndexOf(_arguments, name);
            return index >= 0 && index + 1 < _arguments.Length ? _arguments[index + 1] : null;
        }

        public bool Has(string name) => Array.IndexOf(_arguments, name) >= 0;

        public string[] Values(string name)
        {
            var values = new List<string>();
            for (int index = 0; index + 1 < _arguments.Length; index++)
            {
                if (_arguments[index] == name) values.Add(_arguments[++index]);
            }
            return values.ToArray();
        }

        public int Integer(string name, int fallback)
        {
            string? value = Value(name);
            if (value is null) return fallback;
            if (int.TryParse(value, out int parsed) && parsed >= 0) return parsed;
            throw new ArgumentException($"{name} requires a non-negative integer.");
        }

        public string Required(string name)
        {
            string? value = Value(name);
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"Missing required option {name}.");
            return value;
        }
    }
}
