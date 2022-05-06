﻿// This file is part of the DSharpPlus.ModalCommands project.
// 
// Copyright (c) 2022 Dominic Ris
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NON-INFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Reflection;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.ModalCommands.Attributes;
using DSharpPlus.ModalCommands.Converters;
using DSharpPlus.ModalCommands.EventArgs;
using Emzi0767.Utilities;
using static DSharpPlus.ModalCommands.Converters.EntityConverters;
using static DSharpPlus.ModalCommands.Converters.GeneralConverters;

namespace DSharpPlus.ModalCommands;

public class ModalCommandsExtension : BaseExtension
{
    internal static ModalCommandsConfiguration Config = new();

    private Dictionary<string, ModalCommand> _commands;

    private Dictionary<Type, IModalArgumentConverter> _converters;
    private MethodInfo _convertMethod;
    private MethodInfo _convertToModalIdMethod;

    public event AsyncEventHandler<ModalCommandsExtension, ModalCommandExecutionEventArgs> ModalCommandExecuted
    {
        add => _executed.Register(value);
        remove => _executed.Unregister(value);
    }

    private AsyncEvent<ModalCommandsExtension, ModalCommandExecutionEventArgs> _executed;

    public event AsyncEventHandler<ModalCommandsExtension, ModalCommandErrorEventArgs> ModalCommandErrored
    {
        add => _error.Register(value);
        remove => _error.Unregister(value);
    }

    private AsyncEvent<ModalCommandsExtension, ModalCommandErrorEventArgs> _error;


    public ModalCommandsExtension(ModalCommandsConfiguration config)
    {
        Config = config;
        _commands = new Dictionary<string, ModalCommand>();
        _converters = new Dictionary<Type, IModalArgumentConverter>()
        {
            [typeof(string)] = new StringConverter(),
            [typeof(bool)] = new BoolConverter(),
            [typeof(int)] = new IntConverter(),
            [typeof(uint)] = new UintConverter(),
            [typeof(long)] = new LongConverter(),
            [typeof(ulong)] = new UlongConverter(),
            [typeof(float)] = new FloatConverter(),
            [typeof(double)] = new DoubleConverter(),
            [typeof(DiscordUser)] = new DiscordUserConverter(),
            [typeof(DiscordMember)] = new DiscordMemberConverter(),
            [typeof(DiscordRole)] = new DiscordRoleConverter(),
            [typeof(DiscordChannel)] = new DiscordChannelConverter()
        };

        _convertMethod = typeof(ModalCommandsExtension).GetTypeInfo().DeclaredMethods.FirstOrDefault(xm =>
            xm.Name == "ConvertArgument" && xm.ContainsGenericParameters && !xm.IsStatic) ?? throw new InvalidOperationException();
        _convertToModalIdMethod = typeof(ModalCommandsExtension).GetTypeInfo().DeclaredMethods.FirstOrDefault(xm =>
            xm.Name == "ConvertArgumentToModalId" && xm.ContainsGenericParameters && !xm.IsStatic) ?? throw new InvalidOperationException();
    }

    protected override void Setup(DiscordClient client)
    {
        if (Client is not null) throw new InvalidOperationException("Do NOT run Setup() yourself.");
        client.ModalSubmitted += HandleSubmission;
        _executed = new AsyncEvent<ModalCommandsExtension, ModalCommandExecutionEventArgs>("MODALCOMMAND_EXECUTED", TimeSpan.Zero, null);
        _error = new AsyncEvent<ModalCommandsExtension, ModalCommandErrorEventArgs>("MODALCOMMAND_ERRORED", TimeSpan.Zero, null);
    }

    private async Task HandleSubmission(DiscordClient sender, ModalSubmitEventArgs args)
    {
        var id = args.Interaction.Data.CustomId;
        if (!id.StartsWith(Config.Prefix)) return;
        id = id.Substring(Config.Prefix.Length);

        if (!_commands.ContainsKey(id)) return;
        var command = _commands[id];

        var ctx = BuildContext(sender, args);
        object[] arguments;

        try
        {
            arguments = await BuildArguments(command.Method, args.Values, ctx);
        }
        catch (Exception ex)
        {
            await _error.InvokeAsync(this, new ModalCommandErrorEventArgs()
            {
                ModalId = id,
                Context = ctx,
                Exception = new Exception($"An error has occurred while submitting modal {id}.", ex),
                Handled = false
            });
            return;
        }

        args.Handled = true;

        try
        {
            await (Task)command.Method.Invoke(SpawnInstance(command), arguments);
            await _executed.InvokeAsync(this, new ModalCommandExecutionEventArgs()
            {
                ModalId = id,
                Context = ctx,
                Handled = true
            });
        }
        catch (Exception e)
        {
            await _error.InvokeAsync(this, new ModalCommandErrorEventArgs()
            {
                ModalId = id,
                Context = ctx,
                Exception = new Exception($"An error has occured while executing button command '{command}'.", e),
                Handled = false
            });
        }
    }

    public void RegisterModals<T>() => RegisterModals(typeof(T));

    public void RegisterModals(Type type)
    {
        foreach (var method in type.GetMethods())
        {
            var attr = method.GetCustomAttribute<ModalCommandAttribute>();
            if (attr == null) continue;

            _commands.Add(attr.Name, new ModalCommand
            {
                Method = method,
                Name = attr.Name
            });
        }
    }

    public void RegisterModals(Assembly assembly)
    {
        foreach (var type in assembly.DefinedTypes.Where(t => t.IsPublic && t.IsAssignableTo(typeof(ModalCommandModule))))
        {
            RegisterModals(type);
        }
    }

    private ModalContext BuildContext(DiscordClient client, ModalSubmitEventArgs args) => new()
    {
        Channel = args.Interaction.Channel,
        Client = client,
        Guild = args.Interaction.Guild,
        Interaction = args.Interaction,
        User = args.Interaction.User,
        ModalCommandsExtension = this,
        Values = args.Values.Values.ToArray()
    };

    private async Task<object[]> BuildArguments(MethodInfo method, IReadOnlyDictionary<string, string> values, ModalContext ctx)
    {
        List<object> arguments = new() { ctx };
        for (var i = 1; i < method.GetParameters().Length; i++) arguments.Add(await ConvertArgument(method.GetParameters()[i], values.Values.ToList()[i - 1], ctx));
        return arguments.ToArray();
    }


    private async Task<object> ConvertArgument<T>(string arg, ModalContext ctx)
    {
        if (!_converters.TryGetValue(typeof(T), out var conv))
            throw new ArgumentException($"Unknown argument type '{typeof(T).Name}'");
        if (conv is IModalArgumentConverter<T> converter)
            return (await converter.ConvertAsync(arg, ctx)).Value;

        throw new ArgumentException($"Invalid converter registered for '{typeof(T).Name}'");
    }

    private async Task<object> ConvertArgument(ParameterInfo parameterInfo, string arg, ModalContext ctx)
    {
        var m = _convertMethod.MakeGenericMethod(parameterInfo.ParameterType);
        try
        {
            var task = (Task<object>)m.Invoke(this, new object[] { arg, ctx });
            if (task == null) throw new ArgumentException($"Invalid converter registered for '{parameterInfo.ParameterType.Name}'");
            return await task;
        }
        catch (ArgumentException)
        {
            throw new InvalidCastException($"An argument type of {parameterInfo.ParameterType} is not supported. Try adding a converter using ModalCommandsExtension#AddConverter");
        }
        catch (TargetInvocationException ex)
        {
            if (ex.InnerException is not null)
                throw ex.InnerException;
            throw;
        }
    }

    private object SpawnInstance(ModalCommand command) => InstanceCreator.CreateInstance(command.Method.DeclaringType ?? typeof(ModalCommandModule), Config.Services);


    public void RegisterConverter<T>(IModalArgumentConverter<T> converter)
    {
        if (converter == null) throw new ArgumentNullException(nameof(converter), "Converter cannot be null.");
        if (_converters.ContainsKey(typeof(T))) throw new ArgumentException($"Another converter for {typeof(T).Name} has already been registered.");
        _converters.Add(typeof(T), converter);
    }


    public bool UnregisterConverter<T>() => _converters.Remove(typeof(T));
}