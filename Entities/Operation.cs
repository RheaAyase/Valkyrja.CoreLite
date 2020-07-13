﻿using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Discord.Net;

namespace Valkyrja.entities
{
	public class Operation
	{
		public enum State
		{
			Ready = 0,
			Awaiting,
			AwaitDone,
			Running,
			Finished,
			Canceled
		}

		public const string OperationQueuedString = "This command was placed in a queue for large operations at position `{0}` and will be executed as soon as possible. Should you wish to cancel it at any time, use `!cancel {1}`";

		public CommandArguments CommandArgs = null;
		public State CurrentState = State.Ready;
		public DateTime TimeCreated = DateTime.UtcNow;
		public DateTime TimeStarted = DateTime.MinValue;
		public float AllocatedMemoryStarted = 0f;

		private Operation(CommandArguments commandArgs, float memory)
		{
			this.CommandArgs = commandArgs;
			this.AllocatedMemoryStarted = memory;
			commandArgs.Operation = this;
		}

		/// <summary> Create a new Operation and add it to the queue. </summary>
		public static Operation Create(CommandArguments e)
		{
			Operation op = new Operation(e, GC.GetTotalMemory(false) / 1000000f);
			lock(e.Client.OperationsLock)
				e.Client.CurrentOperations.Add(op);
			return op;
		}

		/// <summary> Execute this operation. </summary>
		public async Task Execute()
		{
			try
			{
				if( await Await(async () => await this.CommandArgs.Channel.SendMessageAsync(string.Format(OperationQueuedString, this.CommandArgs.Client.CurrentOperations.Count, this.CommandArgs.Command.Id))) )
					return;
				this.CurrentState = State.Running;

				await this.CommandArgs.Command.OnExecute(this.CommandArgs);
			}
			catch( HttpException exception )
			{
				await this.CommandArgs.Server.HandleHttpException(exception, $"Failed to execute a command `{this.CommandArgs.CommandId}` in <#{this.CommandArgs.Channel.Id}>");
			}
			catch( Exception exception )
			{
				await this.CommandArgs.Client.LogException(exception, this.CommandArgs);
			}

			Finalise();
		}

		/// <summary> Execute the main loop for this Operation.
		/// Returns true if it was canceled, false otherwise.</summary>
		/// <param name="body">Returning true in the body will break the loop.</param>
		public async Task<bool> While(Func<bool> condition, Func<Task<bool>> body)
		{
			if( condition == null || body == null )
				throw new ArgumentNullException();

			int i = 0;
			int iterationsToYield = this.CommandArgs.Command.Type == CommandType.LargeOperation ? 50 : 10;
			while( condition() )
			{
				if( await AwaitConnection() )
				{
					await this.CommandArgs.Channel.SendMessageAsync($"<@{this.CommandArgs.Message.Author.Id}>, your operation was canceled: `{this.CommandArgs.Command.Id}`");
					return true;
				}

				if( await body() )
					break;

				if( i++ >= iterationsToYield )
					await Task.Yield();
			}

			return false;
		}

		/// <summary> This is blocking call that will await til there are less than config.MaximumConcurrentOperations.
		/// Returns true if it was canceled, false otherwise. </summary>
		public async Task<bool> Await(Func<Task> onAwaitStarted)
		{
			this.CommandArgs.Client.OperationsRan++;

			Operation alreadyInQueue = null;
			lock(this.CommandArgs.Client.OperationsLock)
				alreadyInQueue = this.CommandArgs.Client.CurrentOperations.FirstOrDefault(o => o.CommandArgs.Command.Id == this.CommandArgs.Command.Id && o.CommandArgs.Channel.Id == this.CommandArgs.Channel.Id && o != this);

			if( alreadyInQueue != null )
			{
				lock(this.CommandArgs.Client.OperationsLock)
				{
					this.CommandArgs.Client.CurrentOperations.Remove(this);
					this.CurrentState = State.Canceled;
				}
				return true;
			}

			if( this.CommandArgs.Client.IsAdmin(this.CommandArgs.Message.Author.Id) )
			{
				lock(this.CommandArgs.Client.OperationsLock)
				{
					this.CommandArgs.Client.CurrentOperations.Remove(this);
					this.CommandArgs.Client.CurrentOperations.Insert(0, this);
				}
			}
			else
			{
				bool ShouldAwait()
				{
					int index = 0;
					lock(this.CommandArgs.Client.OperationsLock)
						return !((index = this.CommandArgs.Client.CurrentOperations.IndexOf(this)) < this.CommandArgs.Client.Config.OperationsMax ||
						         (index < this.CommandArgs.Client.Config.OperationsMax + this.CommandArgs.Client.Config.OperationsExtra &&
						          this.CommandArgs.Command.Type == CommandType.Operation)); //Not a large one.
				}

				while( this.CurrentState != State.Canceled && ShouldAwait() )
				{
					if( this.CurrentState == State.Ready )
					{
						this.CurrentState = State.Awaiting;
						if( onAwaitStarted != null )
							await onAwaitStarted();
					}

					await Task.Delay(1000);
				}
			}

			if( this.CurrentState != State.Canceled )
			{
				this.TimeStarted = DateTime.UtcNow;
				this.CurrentState = State.AwaitDone;
			}

			return this.CurrentState == State.Canceled;
		}

		/// <summary> This is blocking call that will await til the connection is peachy.
		/// Returns true if the operation was canceled, false otherwise. </summary>
		public async Task<bool> AwaitConnection()
		{
			while( this.CurrentState != State.Canceled && !this.CommandArgs.Client.IsConnected && this.CurrentState != State.Finished )
				await Task.Delay(1000);

			return this.CurrentState == State.Canceled;
		}

		/// <summary> Gracefully cancel this operation and remove it from the queue. </summary>
		public void Cancel()
		{
			this.CurrentState = State.Canceled;
			lock(this.CommandArgs.Client.OperationsLock)
				this.CommandArgs.Client.CurrentOperations.Remove(this);
		}

		/// <summary> Gracefully finalise this operation and remove it from the list. </summary>
		public void Finalise()
		{
			this.CurrentState = State.Finished;
			lock(this.CommandArgs.Client.OperationsLock)
				this.CommandArgs.Client.CurrentOperations.Remove(this);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public override String ToString()
		{
			return $"Command: `{this.CommandArgs.Command.Id}`\n" +
			       $"Status: `{this.CurrentState}`\n" +
			       $"Author: `{this.CommandArgs.Message.Author.GetUsername()}`\n" +
			       $"Channel: `#{this.CommandArgs.Channel.Name}`\n" +
			       $"TimeCreated: `{Utils.GetTimestamp(this.TimeCreated)}`\n" +
			       $"TimeStarted: `{(this.TimeStarted == DateTime.MinValue ? "0" : Utils.GetTimestamp(this.TimeStarted))}`";
		}
	}
}
