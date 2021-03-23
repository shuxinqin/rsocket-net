using System;
using System.Collections.Generic;
using System.Text;

namespace RSocket.Exceptions
{
	public class RejectedException : RSocketErrorException
	{
		public RejectedException(string message) : base(message)
		{

		}

		public override RSocketProtocol.ErrorCodes ErrorCode => RSocketProtocol.ErrorCodes.Rejected;
	}
}
