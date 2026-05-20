using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NirvanaAPI.Entities;

public class ConfigValue<T> : IConfigValue {
    
    public required string Name { get; init; }
    
    public T? Value;
    private T? _default;
    
    public ConfigValue(T? defaultValue)
    {
        SetDefault(defaultValue);
    }

    public ConfigValue()
    {
    }
    
    public bool EqualsName(string name)
    {
        return Name.Equals(name, StringComparison.OrdinalIgnoreCase);
    }
    
    public void SetDefault(T? defaultValue)
    {
        Value = defaultValue;
        _default = defaultValue;
    }

    public void SetDefaultFrom(object? value)
    {
        SetFrom(value);
        _default = Value;
    }

    public void SetFrom(object? value)
    {
        while (true) {
            
            switch (value) {
                case null:
                    Value = _default;
                    return;
                case T value1:
                    Value = value1;
                    return;
                case JsonNode json:
                    Value = json.GetValue<T>();
                    return;
            }

            if (typeof(T) == typeof(double)) {
                if (value is string stringValue) {
                    Value = (T)(object)double.Parse(stringValue);
                }
                return;
            }

            if (typeof(T) == typeof(float)) {
                if (value is string stringValue) {
                    Value = (T)(object)float.Parse(stringValue);
                }
                return;
            }

            if (typeof(T) == typeof(bool)) {
                if (value is string stringValue) {
                    Value = (T)(object)bool.Parse(stringValue);
                }
                return;
            }

            if (typeof(T) == typeof(int)) {
                if (value is string stringValue) {
                    Value = (T)(object)int.Parse(stringValue);
                }
                return;
            }

            if (typeof(T) == typeof(JsonNode)) {
                if (value is string stringValue) {
                    Value = JsonSerializer.Deserialize<T>(stringValue);
                }

                // Class > JsonNode
                value = JsonSerializer.SerializeToNode(value);
                continue;
            }

            return;
        }
    }

    public object? GetValueTo()
    {
        return Value ?? _default;
    }

    public bool IsDefault()
    {
        if (Value == null) {
            return true;
        }
        return _default != null && _default.Equals(Value);
    }

    public void ToAdd(JsonObject jsonObj)
    {
        JsonNode? jsonNode = GetValueTo() switch {
            string value => value,
            double doubleValue => doubleValue,
            float floatValue => floatValue,
            bool boolValue => boolValue,
            int intValue => intValue,
            JsonNode json => json.AsObject().DeepClone(),
            _ => null
        };
        jsonObj.Add(Name, jsonNode);
    }
    
}