using System;
using System.Reflection;

namespace EL
{
	partial class EspLink
	{
		/// <summary>
		/// Indicates the device that is connected, or null if not connected.
		/// </summary>
		public EspDevice Device { get; private set; }

		/// <summary>
		/// The detected SPI flash size in bytes, or -1 if unknown / not connected. Requires the stub
		/// loader (reads the flash chip id). Exposed so callers in other assemblies can size a chunked
		/// erase without reaching the internal device members.
		/// </summary>
		public int FlashSizeBytes => Device != null ? Device.FlashSize : -1;

		void CreateDevice(uint value, bool isId = false)
		{
			var types = Assembly.GetExecutingAssembly().GetTypes();
			for (int i = 0; i < types.Length; ++i)
			{
				var type = types[i];
				if (typeof(EspDevice).IsAssignableFrom (type.BaseType))
				{
					var attr = type.GetCustomAttribute<EspDeviceAttribute>();
					if (attr != null)
					{
						if ((!isId && attr.Magic == value) || (isId && attr.Id==value))
						{
							Device = (EspDevice)Activator.CreateInstance(type, new object[] { this });
							return;
						}
					}
				}
			}
			throw new NotSupportedException("The connected device is not supported");
		}
	}
}
