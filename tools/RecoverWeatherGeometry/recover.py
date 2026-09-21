#!/usr/bin/env python3
"""Recover immutable weather geometry from the identified retail mesh builders.
No game/Windows process is launched. Only bounded x86 emulation and an isolated
allocator run. The embedded result makes ordinary exports independent of Python.
"""
import argparse
import hashlib
import struct
from pathlib import Path

import pefile
from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_EAX, UC_X86_REG_ECX, UC_X86_REG_EIP, UC_X86_REG_ESP, UC_X86_REG_FPCW

SOURCE_HASH = "7306a001e630f530f945217028ec26b7f430788b73c52cceab944d169ddddf3a"
DEFAULT_OUTPUT = Path(__file__).resolve().parents[2] / "src/Raximod.EngineAssets/Resources/NativeWeatherGeometry.bin"


def recover(executable):
    source = Path(executable).read_bytes()
    if hashlib.sha256(source).hexdigest() != SOURCE_HASH:
        raise ValueError("Unsupported weather executable SHA-256")
    pe = pefile.PE(data=source)
    base = pe.OPTIONAL_HEADER.ImageBase
    image = pe.get_memory_mapped_image()
    machine = Uc(UC_ARCH_X86, UC_MODE_32)
    machine.mem_map(base, (len(image) + 4095) & ~4095)
    machine.mem_write(base, image)
    machine.mem_map(0, 4096)  # Native exception-chain slot; exceptions are never dispatched.
    machine.mem_map(0x2000000, 0x400000)
    stack, body, stop, heap = 0x2010000, 0x2020000, 0x2000000, 0x2100000
    allowed_pages = {0x87e000, 0x87f000, 0x880000, 0x881000, 0x882000, 0xa6a000, 0xa6c000, 0xa77000}
    allocations = []

    def integer(address):
        return struct.unpack("<I", machine.mem_read(address, 4))[0]

    def instruction(_machine, address, _size, _data):
        nonlocal heap
        if address & ~4095 not in allowed_pages:
            raise ValueError(f"Unexpected native weather code address 0x{address:x}")
        if address not in (0xa6a7bb, 0xa6a7b0):
            return
        sp = machine.reg_read(UC_X86_REG_ESP)
        result = 0
        if address == 0xa6a7bb:
            size = integer(sp + 4)
            if not 0 < size < 1_000_000:
                raise ValueError(f"Unexpected weather allocation {size}")
            result = heap
            allocations.append((heap, size))
            heap += (size + 15) & ~15
            if heap >= 0x2400000:
                raise ValueError("Weather allocator limit exceeded")
        machine.reg_write(UC_X86_REG_EAX, result)
        machine.reg_write(UC_X86_REG_ESP, sp + 4)  # cdecl; native caller removes arguments.
        machine.reg_write(UC_X86_REG_EIP, integer(sp))

    machine.hook_add(UC_HOOK_CODE, instruction)
    for entry in (0x87e6e0, 0x87eb40):
        machine.mem_write(stack, struct.pack("<I", stop))
        machine.reg_write(UC_X86_REG_ESP, stack)
        machine.reg_write(UC_X86_REG_ECX, body)
        machine.reg_write(UC_X86_REG_FPCW, 0xf7f)  # Retail RC_CHOP, extended precision.
        machine.emu_start(entry, stop, timeout=10_000_000, count=2_000_000)
        if machine.reg_read(UC_X86_REG_EIP) != stop:
            raise ValueError("Weather builder exceeded execution budget")
    output = bytearray(struct.pack("<4sII", b"PSWC", 1, 2))
    for offset, expected in ((0x54, (526, 3000)), (0x64, (385, 1920))):
        vertices, indices = struct.unpack("<II", machine.mem_read(body + offset, 8))
        count_i, count_v, triangles = struct.unpack("<HHH", machine.mem_read(body + offset + 8, 6))
        if (count_v, count_i) != expected or count_i != 3 * triangles:
            raise ValueError("Unexpected native weather topology")
        if (vertices, count_v * 24) not in allocations or (indices, count_i * 2) not in allocations:
            raise ValueError("Weather geometry exceeds its native allocation")
        output.extend(struct.pack("<II", count_v, count_i))
        output.extend(machine.mem_read(vertices, count_v * 24))  # XYZ, packed diffuse, UV. Native Z-up.
        output.extend(machine.mem_read(indices, count_i * 2))
    return bytes(output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=Path)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    data = recover(args.executable)
    if args.check:
        if args.out.read_bytes() != data:
            raise ValueError("Embedded weather geometry differs from original instructions")
    elif not args.out.exists() or args.out.read_bytes() != data:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_bytes(data)
    print(f"Weather geometry: {len(data)} bytes, SHA-256 {hashlib.sha256(data).hexdigest()}")


if __name__ == "__main__":
    main()
