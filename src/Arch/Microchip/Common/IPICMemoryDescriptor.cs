﻿#region License
/* 
 * Copyright (C) 2017-2018 Christian Hostelet.
 * inspired by work of:
 * Copyright (C) 1999-2017 John Källén.
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2, or (at your option)
 * any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; see the file COPYING.  If not, write to
 * the Free Software Foundation, 675 Mass Ave, Cambridge, MA 02139, USA.
 */
#endregion

using Reko.Core.Expressions;
using Reko.Core;
using Reko.Libraries.Microchip;

namespace Reko.Arch.Microchip.Common
{

    /// <summary>
    /// Interface for PIC memory definitions.
    /// </summary>
    public interface IPICMemoryDescriptor
    {
        /// <summary>
        /// Gets the PIC memory map (definitions of memory regions, translation of addresses).
        /// </summary>
        IMemoryMap MemoryMap { get; }

        /// <summary>
        /// Gets the PIC Device Configuration Registers definitions.
        /// </summary>
        IDeviceConfigDefs DeviceConfigDefinitions { get; }

        /// <summary>
        /// Gets or sets the PIC execution mode.
        /// </summary>
        /// <value>
        /// The PIC execution mode.
        /// </value>
        PICExecMode ExecMode { get; set; }

    }

}
