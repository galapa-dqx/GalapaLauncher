#include "disasm.h"

static int modrm_extra(const uint8_t *p) {
    uint8_t modrm = p[0];
    int mod = modrm >> 6;
    int rm  = modrm & 7;
    int n   = 1;                          // ModRM itself
    if (mod == 3) return n;               // register-register, no extras
    if (rm == 4)  n++;                    // SIB byte present
    if (mod == 1) n += 1;                 // disp8
    else if (mod == 2) n += 4;            // disp32
    else if (mod == 0 && rm == 5) n += 4; // [disp32]
    return n;
}

int insn_len32(const uint8_t *p) {
    uint8_t op = p[0];

    if ((op >= 0x50 && op <= 0x5F) ||     // push/pop reg
        op == 0x90 || op == 0xC9 || op == 0xC3 || op == 0xCB || op == 0xCC ||
        op == 0x06 || op == 0x07 || op == 0x0E || op == 0x16 || op == 0x17 ||
        op == 0x1E || op == 0x1F || op == 0x9C || op == 0x9D ||
        op == 0x60 || op == 0x61 || op == 0xF8 || op == 0xF9 || op == 0xFA || op == 0xFB)
        return 1;

    if ((op >= 0x70 && op <= 0x7F) ||     // Jcc rel8
        op == 0xEB || op == 0xE0 || op == 0xE1 || op == 0xE2 || op == 0xE3 ||
        op == 0x6A || (op >= 0xB0 && op <= 0xB7))
        return 2;

    if (op == 0xC2 || op == 0xCA) return 3;

    if (op == 0xE8 || op == 0xE9 || op == 0x68 ||
        op == 0xA1 || op == 0xA3 || (op >= 0xB8 && op <= 0xBF))
        return 5;

    if (op == 0xA0 || op == 0xA2) return 2;

    if (op == 0x85 || op == 0x87 ||
        op == 0x01 || op == 0x03 || op == 0x09 || op == 0x0B ||
        op == 0x11 || op == 0x13 || op == 0x19 || op == 0x1B ||
        op == 0x21 || op == 0x23 || op == 0x29 || op == 0x2B ||
        op == 0x31 || op == 0x33 || op == 0x39 || op == 0x3B ||
        op == 0x89 || op == 0x8B || op == 0x8D || op == 0x8F ||
        op == 0xFF || op == 0xD3)
        return 1 + modrm_extra(p + 1);

    if (op == 0x83 || op == 0x80 || op == 0xC0 || op == 0xC1 ||
        op == 0xD0 || op == 0xD1 || op == 0x6B)
        return 1 + modrm_extra(p + 1) + 1;

    if (op == 0x81 || op == 0x69)
        return 1 + modrm_extra(p + 1) + 4;

    if (op == 0x0F) {
        uint8_t op2 = p[1];
        if (op2 >= 0x80 && op2 <= 0x8F) return 6;
        int n = 2 + modrm_extra(p + 2);
        if (op2 == 0xA4 || op2 == 0xAC || op2 == 0xBA ||
            op2 == 0xC2 || op2 == 0xC4 || op2 == 0xC5 || op2 == 0xC6)
            n += 1;
        return n;
    }

    return 1; // unknown — safe fallback
}
