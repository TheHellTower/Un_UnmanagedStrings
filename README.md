# Un_UnmanagedStrings

[![CodeFactor](https://www.codefactor.io/repository/github/thehelltower/UnUnmanagedStrings/badge)](https://www.codefactor.io/repository/github/thehelltower/UnUnmanagedStrings)

## 📜 What is Un_UnmanagedStrings ?

**Un_UnmanagedStrings** is a dnlib-based .NET post-processing tool that **reverses the string protection applied by UnmanagedString**.

It scans a protected assembly for native pointer-based string reconstruction patterns and restores them back into regular managed `ldstr` instructions.

In practice, the tool:

1. Detects injected **native methods** that return pointers to embedded string data
2. Reads the raw string bytes directly from the PE file
3. Decodes the data (ASCII or Unicode)
4. Replaces the native call + `string` constructor sequence with a single `ldstr`
5. Removes the now-unused native methods

The result is a clean, readable assembly with managed string literals restored.

The project is implemented **purely with dnlib**, without ConfuserEx or ASMResolver.

---

## ⚠️ Disclaimer

This project is intended for **educational, research, and defensive purposes**, such as:

- Learning how native-backed string protections work
- Reversing your own protected binaries
- Malware analysis and reverse engineering research

You are responsible for how you use this tool.

Do **not** use it to:
- Reverse software you do not own or have permission to analyze
- Circumvent licensing, DRM, or copy-protection
- Assist in malicious reverse engineering

Always comply with local laws and software licenses.

---

## 🎯 Key Features

- ✅ **Purpose-built for UnmanagedString output**  
  Targets the exact native stub and IL patterns produced by [UnmanagedString](https://github.com/TheHellTower/UnmanagedString)

- ✅ **x86 and x64 support**  
  Correctly decodes native string blobs for both architectures

- ✅ **ASCII and Unicode decoding**  
  Supports both `sbyte*` and `char*` return types

- ✅ **Multiple constructor patterns supported**  
  - `string(sbyte*/char*)`  
  - `string(sbyte*/char*, int start, int length)`

- ✅ **Direct RVA-based decoding**  
  Reads string data directly from the PE image

- ✅ **IL cleanup**  
  Restores `ldstr` instructions and removes dead native methods

---

## 🔍 Example

**Protected:**
```csharp
Console.WriteLine(new string(<Module>.00ffc87a1ad544b6a1f00671960148e5()));
````

**Restored:**

```csharp
Console.WriteLine("Hello, world!");
```

The native method and pointer-based reconstruction are removed, leaving a standard managed string literal.

---

## 🧠 How it works internally

At a high level, Un_UnmanagedStrings performs **static recovery** of unmanaged strings by analyzing both **IL patterns** and **native method layout**.

### 1️⃣ Native method detection

The tool scans all methods and identifies candidates that:

* Are declared on the `<Module>` global type
* Are marked as `native`
* Return a pointer type (`sbyte*` or `char*`)
* Have a valid method RVA

These methods are assumed to be string providers.

---

### 2️⃣ Native stub verification

To avoid false positives, the native method body is validated against a known **position-independent stub**:

* **x86**: `call/pop` sequence that computes the data address
* **x64**: `lea rax, [rip+...]` + `ret`

If the stub does not match exactly, the method is ignored.

---

### 3️⃣ Extracting string bytes

Once a stub is validated:

1. The reader skips the stub instructions
2. Reads raw bytes directly from the PE image
3. Stops at a null terminator or known length
4. Decodes the data using ASCII or Unicode based on the return type

This yields the original string content.

---

### 4️⃣ IL pattern replacement

The decoder then looks for IL sequences of the form:

```il
call   native int8*/char* <Module>::GetString()
[newobj string::.ctor(...)]
```

When matched:

* The constructor call is removed
* The native call is replaced with `ldstr "decoded string"`
* Stack behavior is preserved

---

### 5️⃣ Cleanup

After all references are removed, unused native string methods can be safely deleted from the module, restoring a clean managed assembly.

---

## ⚠️ Limitations & Known Issues

* ❗ **Stub-dependent**
  Only works for binaries produced by **this exact UnmanagedString implementation**.
  Any change to the native stub breaks decoding.

* ❗ **No runtime emulation**
  Strings are recovered statically; dynamic or encrypted native strings are not supported.

* ❗ **Encoding assumptions**
  Assumes ASCII or UTF-16LE encoding based on pointer type.

* ❗ **Custom constructors not supported**
  Non-standard `string` construction logic will be skipped.

* ❗ **Pointer arithmetic variants**
  Modified or obfuscated pointer math in the native stub will not be recognized.

* ❗ **Aggressive native stripping**
  Removing native methods is safe only if all references are correctly resolved.

---

## 🧪 Testing checklist

* Test only on assemblies produced by your **UnmanagedString**
* Verify restored strings in dnSpy / ILSpy
* Run the decoded binary to confirm identical behavior
* Confirm no remaining native methods are referenced
* Test both x86 and x64 protected samples

---

## 🔄 Related Projects

- [UnmanagedString](https://github.com/TheHellTower/UnmanagedString) = A dnlib-based .NET tool that relocates managed string literals into **native code**, embedding string bytes in the PE and reconstructing them at runtime. This project is the original encoder that *Un_UnmanagedStrings* is designed to reverse.

---

## 📢 Credits

* [dnlib](https://github.com/0xd4d/dnlib) — .NET Module/Assembly Reader/Writer Library
* [UnmanagedString](https://github.com/TheHellTower/UnmanagedString) — Protection this project is designed to reverse