using System.Runtime.InteropServices;
using System.Text;

namespace MyloMail.Api.Credentials;

/// <summary>Direct binding to macOS Keychain Services for generic-password entries.</summary>
internal static class MacKeychainCredentialStore
{
	private const int Success = 0;
	private const int ItemNotFound = -25_300;
	private const int DuplicateItem = -25_299;
	private static readonly nint security = NativeLibrary.Load("/System/Library/Frameworks/Security.framework/Security");
	private static readonly nint coreFoundation = NativeLibrary.Load("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation");
	private static readonly nint itemClass = SecurityConstant("kSecClassGenericPassword");
	private static readonly nint classKey = SecurityConstant("kSecClass");
	private static readonly nint serviceKey = SecurityConstant("kSecAttrService");
	private static readonly nint accountKey = SecurityConstant("kSecAttrAccount");
	private static readonly nint valueDataKey = SecurityConstant("kSecValueData");
	private static readonly nint returnDataKey = SecurityConstant("kSecReturnData");
	private static readonly nint trueValue = CoreFoundationConstant("kCFBooleanTrue");

	public static void Store(string service, Guid accountId, string encoded, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		using var query = Query(service, accountId);
		var data = Data(Encoding.UTF8.GetBytes(encoded));
		using var attributes = new NativeDictionary([(valueDataKey, data)], [data]);
		var status = SecItemUpdate(query.Handle, attributes.Handle);
		if (status == Success) return;
		if (status != ItemNotFound) Throw(status, "update");

		using var addQuery = Query(service, accountId, encoded);
		status = SecItemAdd(addQuery.Handle, nint.Zero);
		if (status == Success) return;
		if (status == DuplicateItem)
		{
			status = SecItemUpdate(query.Handle, attributes.Handle);
			if (status == Success) return;
		}
		Throw(status, "store");
	}

	public static string? Retrieve(string service, Guid accountId, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		using var query = Query(service, accountId, includeReturnData: true);
		var status = SecItemCopyMatching(query.Handle, out var result);
		if (status == ItemNotFound) return null;
		if (status != Success) Throw(status, "retrieve");
		try
		{
			var length = checked((int)CFDataGetLength(result));
			var bytes = new byte[length];
			Marshal.Copy(CFDataGetBytePtr(result), bytes, 0, length);
			return Encoding.UTF8.GetString(bytes);
		}
		finally
		{
			CFRelease(result);
		}
	}

	public static void Delete(string service, Guid accountId, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		using var query = Query(service, accountId);
		var status = SecItemDelete(query.Handle);
		if (status is Success or ItemNotFound) return;
		Throw(status, "delete");
	}

	private static NativeDictionary Query(string service, Guid accountId, string? encoded = null, bool includeReturnData = false)
	{
		var serviceValue = String(service);
		var accountValue = String(accountId.ToString("N"));
		var entries = new List<(nint Key, nint Value)>
		{
			(classKey, itemClass),
			(serviceKey, serviceValue),
			(accountKey, accountValue),
		};
		if (encoded is not null) entries.Add((valueDataKey, Data(Encoding.UTF8.GetBytes(encoded))));
		if (includeReturnData) entries.Add((returnDataKey, trueValue));
		return new NativeDictionary(entries, [serviceValue, accountValue, .. entries.Skip(3).Where(entry => entry.Key == valueDataKey).Select(entry => entry.Value)]);
	}

	private static nint String(string value) => CFStringCreateWithCString(nint.Zero, value, 0x08000100);
	private static nint Data(byte[] value) => CFDataCreate(nint.Zero, value, value.Length);
	private static nint SecurityConstant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(security, name));
	private static nint CoreFoundationConstant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(coreFoundation, name));
	private static void Throw(int status, string operation) => throw new InvalidOperationException($"Keychain {operation} failed with status {status}.");

	private sealed class NativeDictionary : IDisposable
	{
		private readonly nint[] ownedValues;
		public nint Handle { get; }

		public NativeDictionary(IReadOnlyList<(nint Key, nint Value)> entries, nint[]? ownedValues = null)
		{
			this.ownedValues = ownedValues ?? [];
			Handle = CFDictionaryCreate(nint.Zero, entries.Select(entry => entry.Key).ToArray(), entries.Select(entry => entry.Value).ToArray(), entries.Count, nint.Zero, nint.Zero);
			if (Handle == nint.Zero) throw new InvalidOperationException("Could not create a Keychain query.");
		}

		public void Dispose()
		{
			CFRelease(Handle);
			foreach (var value in ownedValues) CFRelease(value);
		}
	}

	[DllImport("/System/Library/Frameworks/Security.framework/Security")]
	private static extern int SecItemAdd(nint attributes, nint result);
	[DllImport("/System/Library/Frameworks/Security.framework/Security")]
	private static extern int SecItemCopyMatching(nint query, out nint result);
	[DllImport("/System/Library/Frameworks/Security.framework/Security")]
	private static extern int SecItemUpdate(nint query, nint attributesToUpdate);
	[DllImport("/System/Library/Frameworks/Security.framework/Security")]
	private static extern int SecItemDelete(nint query);
	[DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", CharSet = CharSet.Ansi)]
	private static extern nint CFStringCreateWithCString(nint allocator, string value, uint encoding);
	[DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
	private static extern nint CFDataCreate(nint allocator, byte[] bytes, nint length);
	[DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
	private static extern nint CFDictionaryCreate(nint allocator, nint[] keys, nint[] values, nint count, nint keyCallbacks, nint valueCallbacks);
	[DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
	private static extern nint CFDataGetLength(nint data);
	[DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
	private static extern nint CFDataGetBytePtr(nint data);
	[DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
	private static extern void CFRelease(nint cf);
}
